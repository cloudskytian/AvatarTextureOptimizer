using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Stage: animation analysis & application. / 阶段：动画分析与应用。
    /// Collects all reachable clips, parses curves, then applies the results: registers materials
    /// & textures swapped in by animations, whitelists ST-animated textures, applies worst-case
    /// cutout/blend flags, drops never-enabled renderers, and computes animated scale bounds. /
    /// 收集全部可达剪辑、解析曲线并应用结果：登记动画切换进来的材质与贴图、白名单化 ST 动画贴图、
    /// 应用最严苛 cutout/blend 标记、剔除永不启用的渲染器、计算动画缩放边界。
    /// </summary>
    internal sealed class AtoStageAnimations : IAtoStage
    {
        public string I18nKey => "animations";

        public void Run(AtoContext ctx)
        {
            var info = ctx.Animations;

            AnimatorScanner.Collect(ctx);
            ctx.State.ThrowIfCancelled();

            // ---- read-only clips (user assets, not cloned by MA): their references cannot be
            // remapped → whitelist everything they reference. / 只读剪辑（用户资产，未被 MA 克隆）：
            // 其引用无法重映射 → 其引用的对象全部白名单。
            var containerPath = ctx.Ndmf.AssetContainer != null
                ? UnityEditor.AssetDatabase.GetAssetPath(ctx.Ndmf.AssetContainer)
                : "";
            var readonlyCount = 0;
            foreach (var clip in info.Clips)
            {
                var path = UnityEditor.AssetDatabase.GetAssetPath(clip);
                if (string.IsNullOrEmpty(path)) continue; // in-memory clone → editable. / 内存克隆 → 可编辑。
                if (!string.IsNullOrEmpty(containerPath) && path.StartsWith(containerPath + "/")) continue; // build folder → editable. / 构建目录 → 可编辑。
                readonlyCount++;
                AtoLog.Verbose($"[ATO] readonly animation clip: {clip.name} ({path}) — its references are whitelisted.");
                foreach (var binding in UnityEditor.AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    foreach (var key in UnityEditor.AnimationUtility.GetObjectReferenceCurve(clip, binding))
                    {
                        if (key.value is Texture2D readonlyTexture)
                        {
                            ctx.WhitelistTexture(readonlyTexture, $"referenced by readonly clip {clip.name}");
                        }
                        if (key.value is Material readonlyMaterial)
                        {
                            ctx.WhitelistObjects.Add(readonlyMaterial);
                        }
                    }
                }
            }
            if (readonlyCount > 0)
            {
                ctx.Warn($"[ATO] {readonlyCount} animation clip(s) are readonly (not cloned): their referenced objects are whitelisted.");
            }

            // ---- materials that are direct animation targets cannot be cloned (the clip stores
            // the material reference internally) → their textures are whitelisted. /
            // 直接动画目标的材质无法克隆（剪辑内部存有材质引用）→ 其贴图白名单。
            foreach (var material in info.DirectAnimatedMaterials)
            {
                foreach (var record in ctx.Textures.Values)
                {
                    foreach (var slot in record.Slots)
                    {
                        if (slot.Material == material)
                        {
                            ctx.WhitelistTexture(slot.Texture,
                                $"material {material.name} is a direct animation target");
                        }
                    }
                }
            }

            // ---- (renderer, slot) → AtoMaterialSlot lookup ----
            var slotLookup = new Dictionary<(Renderer, int), AtoMaterialSlot>();
            foreach (var data in ctx.Renderers)
            {
                foreach (var slot in data.Slots)
                {
                    slotLookup[(data.Renderer, slot.Index)] = slot;
                }
            }

            // ---- material slot options from animations ----
            foreach (var kv in info.SlotMaterialOptions)
            {
                var (renderer, slotIndex) = kv.Key;
                if (!slotLookup.TryGetValue(kv.Key, out var slot)) continue;

                foreach (var material in kv.Value)
                {
                    if (material == null || slot.AnimatedOptions.Contains(material)) continue;
                    slot.AnimatedOptions.Add(material);
                    // Register the swapped-in material's textures on this slot. / 登记切换进来的材质在该槽上的贴图。
                    AtoSlotFactory.RegisterMaterialTextures(ctx, slot.RendererData, slotIndex, material);
                }
                if (slot.AnimatedOptions.Count > 1)
                {
                    // Individually-switched slot: no slot merging for it. / 被单独切换的槽：禁止槽合并。
                    slot.IndividuallyAnimated = true;
                }
            }

            // ---- slots with animated properties cannot be merged ----
            foreach (var (renderer, slotIndex) in info.AnimatedSlotProperties)
            {
                if (slotLookup.TryGetValue((renderer, slotIndex), out var slot))
                {
                    slot.IndividuallyAnimated = true;
                }
            }

            // ---- texture swaps on material properties ----
            foreach (var kv in info.TextureSwaps)
            {
                var (material, property) = kv.Key;
                if (material == null) continue;

                // Find existing slots with this (material, property) to learn where they're assigned. /
                // 找到该（材质, 属性）的既有槽，得知其所在位置。
                var assigned = new List<(AtoRendererData data, int slotIndex)>();
                foreach (var record in ctx.Textures.Values)
                {
                    foreach (var slot in record.Slots)
                    {
                        if (slot.Material == material && slot.PropertyName == property)
                        {
                            foreach (var pos in slot.AssignedSlots) assigned.Add(FindRendererData(ctx, pos));
                        }
                    }
                }

                foreach (var texture in kv.Value)
                {
                    foreach (var (data, slotIndex) in assigned)
                    {
                        if (data != null)
                        {
                            var slot = AtoSlotFactory.GetOrCreateSlot(ctx, data, slotIndex, material, property, texture);
                            if (slot != null) slot.Usage.Animated = true;
                        }
                    }
                }
            }

            // ---- ST-animated textures → whitelist ----
            foreach (var (material, property) in info.AnimatedSt)
            {
                var found = false;
                foreach (var record in ctx.Textures.Values)
                {
                    foreach (var slot in record.Slots)
                    {
                        if (slot.Material == material && slot.PropertyName == property)
                        {
                            ctx.WhitelistTexture(slot.Texture,
                                $"{material.name}.{property} has animated ST transform");
                            found = true;
                        }
                    }
                }
                if (found)
                {
                    ctx.Warn(ctx.State.Tr("warn.stAnimated", material.name, property));
                }
            }

            // ---- animated cutoffs & keywords → strictest alpha usage ----
            foreach (var (material, values) in info.AnimatedCutoffs)
            {
                foreach (var record in ctx.Textures.Values)
                {
                    foreach (var slot in record.Slots)
                    {
                        if (slot.Material != material) continue;
                        foreach (var cutoff in values)
                        {
                            if (slot.Usage.CutoutThresholds.All(c => c.material != material || Mathf.Abs(c.cutoff - cutoff) > 1e-4f))
                            {
                                slot.Usage.CutoutThresholds.Add((material, cutoff));
                            }
                        }
                    }
                }
            }
            var keywordMaterials = new HashSet<Material>(info.AnimatedKeywords.Select(k => k.Item1));
            foreach (var material in keywordMaterials)
            {
                foreach (var record in ctx.Textures.Values)
                {
                    foreach (var slot in record.Slots)
                    {
                        if (slot.Material == material)
                        {
                            // Render mode may switch to transparent → assume blend (strictest). /
                            // 渲染模式可能切到透明 → 按 blend 处理（最严苛）。
                            slot.Usage.HasBlend = true;
                        }
                    }
                }
            }

            // ---- animated scale bounds (own object + animated ancestors) ----
            foreach (var data in ctx.Renderers)
            {
                var scale = Vector3.one;
                var current = data.Renderer.transform;
                while (current != null)
                {
                    if (info.AnimatedScaleObjects.Contains(current.gameObject) &&
                        info.MaxLocalScale.TryGetValue(current.gameObject, out var local))
                    {
                        scale = Vector3.Scale(scale, local);
                    }
                    current = current.parent;
                }
                data.MaxAnimatedScale = scale;
                foreach (var uvGroup in data.UvGroups.Values)
                {
                    uvGroup.MaxAnimatedScale = scale;
                }
            }

            // ---- drop renderers that are never enabled / never active ----
            var dropped = new List<AtoRendererData>();
            foreach (var data in ctx.Renderers)
            {
                var animatedEnabled = info.AnimatedEnabled.Contains(data.Renderer);
                var activeAnimated = IsActiveAnimated(data.Renderer.gameObject, info);

                var initiallyEnabled = data.EffectivelyEnabled && IsActiveChain(data.Renderer.gameObject);
                if (!initiallyEnabled && !animatedEnabled && !activeAnimated)
                {
                    dropped.Add(data);
                }
            }
            foreach (var data in dropped)
            {
                AtoLog.Verbose($"[ATO] dropping never-enabled renderer {data.Renderer.name}");
                foreach (var uvGroup in data.UvGroups.Values)
                {
                    ctx.UvGroups.Remove(uvGroup);
                    // Remove its slots from texture records. / 从贴图记录中移除其槽位。
                    foreach (var slot in uvGroup.Slots)
                    {
                        if (ctx.Textures.TryGetValue(slot.Texture, out var record))
                        {
                            record.Slots.Remove(slot);
                        }
                    }
                }
                ctx.Renderers.Remove(data);
            }

            // ---- drop texture records with no remaining slots ----
            var emptyTextures = ctx.Textures.Where(kv => kv.Value.Slots.Count == 0).Select(kv => kv.Key).ToList();
            foreach (var texture in emptyTextures) ctx.Textures.Remove(texture);

            // ---- rebuild type groups (new slots joined) ----
            AtoStageScan.BuildTypeGroups(ctx);

            AtoLog.Info($"[ATO] animations applied: {info.SlotMaterialOptions.Count} animated slot(s), " +
                        $"{info.TextureSwaps.Count} texture swap(s), {info.AnimatedSt.Count} ST animation(s), " +
                        $"{dropped.Count} renderer(s) dropped.");
        }

        private static (AtoRendererData, int) FindRendererData(AtoContext ctx, (Renderer renderer, int slotIndex) pos)
        {
            foreach (var data in ctx.Renderers)
            {
                if (data.Renderer == pos.renderer) return (data, pos.slotIndex);
            }
            return (null, -1);
        }

        /// <summary>Whether the GameObject or any ancestor's active state is animated. / 物体或祖先的激活状态是否被动画。</summary>
        private static bool IsActiveAnimated(GameObject go, AtoAnimationInfo info)
        {
            var current = go.transform;
            while (current != null)
            {
                if (info.AnimatedActive.Contains(current.gameObject)) return true;
                current = current.parent;
            }
            return false;
        }

        /// <summary>Whether the GameObject is active along the whole chain (self + parents). / 物体整条链（自身+父级）是否激活。</summary>
        private static bool IsActiveChain(GameObject go)
        {
            var current = go.transform;
            while (current != null)
            {
                if (!current.gameObject.activeSelf) return false;
                current = current.parent;
            }
            return true;
        }
    }
}
