using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Fosa.AvatarTextureOptimizer.Editor.Islands;

namespace Fosa.AvatarTextureOptimizer.Editor.Apply
{
    // 材质应用器：为引用被替换贴图的材质生成克隆（每材质一个克隆：同一贴图的所有岛在同一图集 → 克隆内容全局一致），
    // 仅修改贴图属性、绝不修改其他着色器参数；材质内容/参数完全相同且动画不单独切换 → 去重；
    // 通过 ObjectRegistry 注册替换（NDMF 自动重写动画中的材质引用）。
    // Material applier: clones materials that reference replaced textures (one clone per material — all islands of a texture
    // share one atlas, so clone content is globally consistent); only texture properties change, never other shader parameters.
    // Identical materials (not separately swapped by animations) are deduplicated; replacements registered via ObjectRegistry.
    internal static class MaterialApplier
    {
        public static void Apply(ATOContext ctx, ATOReport.Stage stage)
        {
            // 1) 材质去重（内容与参数完全相同 + 动画不单独切换）。Material dedup.
            var canonical = Deduplicate(ctx, stage);

            // 2) 收集需要克隆的材质（基础 + 动画切换材质）与替换关系。
            // Collect materials to clone (base + animated swaps) and their replacements.
            var clones = new Dictionary<Material, Material>();
            var needsClone = new HashSet<Material>();

            foreach (var slot in ctx.slots)
            {
                ctx.CheckCancelled();
                var mat = ResolveCanonical(canonical, slot.material);
                slot.material = mat;
                foreach (var use in slot.uses)
                {
                    if (use.texture == null) continue;
                    if (GetReplacement(ctx, use) != null)
                    {
                        var m = ResolveCanonical(canonical, use.sourceMaterial);
                        if (m != null) needsClone.Add(m);
                    }
                }
            }

            // 3) 克隆并设置贴图属性（仅贴图，不动其他参数）。Clone and set texture properties only.
            foreach (var mat in needsClone)
            {
                ctx.CheckCancelled();
                var clone = new Material(mat) { name = mat.name + "_ATO" };
                clones[mat] = clone;
                foreach (var slot in ctx.slots)
                {
                    foreach (var use in slot.uses)
                    {
                        if (use.texture == null) continue;
                        if (ResolveCanonical(canonical, use.sourceMaterial) != mat) continue;
                        var replacement = GetReplacement(ctx, use);
                        if (replacement == null) continue;
                        if (!string.IsNullOrEmpty(use.propertyName) && use.propertyName != "animated")
                        {
                            clone.SetTexture(use.propertyName, replacement);
                        }
                    }
                }
                ctx.ndmf.ObjectRegistry.RegisterReplacedObject(mat, clone);
            }

            // 4) 槽位材质（克隆优先）。Slot materials (clones preferred).
            var slotMaterials = new Dictionary<Analysis.SlotEntry, Material>();
            foreach (var slot in ctx.slots)
            {
                var mat = slot.material;
                Material clone;
                slotMaterials[slot] = clones.TryGetValue(mat, out clone) ? clone : mat;
            }

            // 3) 设置渲染器 sharedMaterials（含槽位合并后的数量；合并由 SlotMerger 先行完成）。
            // Assign renderer sharedMaterials (slot counts already merged by SlotMerger).
            var byRenderer = new Dictionary<Renderer, List<Analysis.SlotEntry>>();
            foreach (var slot in ctx.slots)
            {
                List<Analysis.SlotEntry> list;
                if (!byRenderer.TryGetValue(slot.renderer, out list))
                {
                    list = new List<Analysis.SlotEntry>();
                    byRenderer[slot.renderer] = list;
                }
                list.Add(slot);
            }

            foreach (var kv in byRenderer)
            {
                ctx.CheckCancelled();
                var renderer = kv.Key;
                var slots = kv.Value;
                int maxIndex = 0;
                foreach (var s in slots) maxIndex = Mathf.Max(maxIndex, s.slotIndex);
                var arr = new Material[maxIndex + 1];
                foreach (var s in slots) arr[s.slotIndex] = slotMaterials[s];
                renderer.sharedMaterials = arr;
            }

            // 5) 纹理替换注册（仅当旧贴图只被一个替换物替换时注册；多替换物由动画重写器处理）。
            // Texture replacement registration (only when a texture maps to exactly one replacement).
            RegisterTextureReplacements(ctx);

            stage.AddLine(string.Format(ATOLocalization.Tr("log.materialApply"), clones.Count, canonical.Count));
        }

        // 去重映射解析（无映射返回原材质）。Dedup-map resolution (identity when unmapped).
        private static Material ResolveCanonical(Dictionary<Material, Material> canonical, Material mat)
        {
            if (mat == null) return null;
            Material c;
            if (canonical.TryGetValue(mat, out c)) return c;
            return mat;
        }

        // 槽位使用的替换贴图：该槽位岛上对应使用的图集；否则条目级替换；否则原贴图。
        // Replacement for a slot use: the atlas of the matching island use; else the entry-level replacement; else the source.
        internal static Texture2D GetReplacement(ATOContext ctx, Analysis.TextureUse use)
        {
            var entry = use.texture;
            foreach (var e in ctx.islandEntities)
            {
                if (e.mesh != use.slot.mesh || e.uvChannel != use.uvChannel || e.submesh != use.slot.slotIndex) continue;
                if (e.noAtlasFallback || e.whitelistedFull) continue;
                foreach (var u in e.uses)
                {
                    if (u.texture == entry && u.replacementTexture != null) return u.replacementTexture;
                }
            }
            if (entry.replacementTexture != null) return entry.replacementTexture;
            return null;
        }

        // 材质去重：内容和参数完全相同 + 动画不单独切换 → 合并并注册。
        // Material dedup: identical content & parameters + not separately swapped by animations → merge and register.
        private static Dictionary<Material, Material> Deduplicate(ATOContext ctx, ATOReport.Stage stage)
        {
            var result = new Dictionary<Material, Material>();
            if (!ctx.settings.deduplicateMaterials) return result;

            var candidates = new List<Material>();
            foreach (var slot in ctx.slots)
            {
                foreach (var m in slot.sourceMaterials)
                {
                    if (m != null && !candidates.Contains(m)) candidates.Add(m);
                }
            }

            int merged = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                ctx.CheckCancelled();
                var a = candidates[i];
                if (result.ContainsKey(a)) continue;
                if (IsSwapSensitive(ctx, a)) continue;
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    var b = candidates[j];
                    if (result.ContainsKey(b)) continue;
                    if (IsSwapSensitive(ctx, b)) continue;
                    if (!MaterialEquals(a, b)) continue;
                    result[b] = a;
                    ctx.ndmf.ObjectRegistry.RegisterReplacedObject(b, a);
                    stage.AddLine(string.Format(ATOLocalization.Tr("log.materialDedup"), b.name, a.name));
                    merged++;
                }
            }
            if (merged > 0) stage.AddLine(string.Format(ATOLocalization.Tr("log.materialDedupSummary"), merged));
            return result;
        }

        // 动画是否单独切换该材质（槽位切换涉及 → 保守跳过去重）。Whether animations swap this material individually.
        private static bool IsSwapSensitive(ATOContext ctx, Material m)
        {
            foreach (var kv in ctx.animations.slotSwapMaterials)
            {
                if (kv.Value.Contains(m)) return true;
                if (kv.Key.material == m) return true;
            }
            return false;
        }

        // 材质内容与参数完全一致比较（着色器全部属性 + 关键字 + 渲染队列）。
        // Full material content & parameter comparison (all shader properties + keywords + render queue).
        private static bool MaterialEquals(Material a, Material b)
        {
            if (a.shader != b.shader) return false;
            if (a.renderQueue != b.renderQueue) return false;
            int n = ShaderUtil.GetPropertyCount(a.shader);
            for (int i = 0; i < n; i++)
            {
                var name = ShaderUtil.GetPropertyName(a.shader, i);
                var type = ShaderUtil.GetPropertyType(a.shader, i);
                switch (type)
                {
                    case ShaderUtil.ShaderPropertyType.Color:
                        if (a.GetColor(name) != b.GetColor(name)) return false;
                        break;
                    case ShaderUtil.ShaderPropertyType.Float:
                    case ShaderUtil.ShaderPropertyType.Range:
                        if (!Mathf.Approximately(a.GetFloat(name), b.GetFloat(name))) return false;
                        break;
                    case ShaderUtil.ShaderPropertyType.Vector:
                        if (a.GetVector(name) != b.GetVector(name)) return false;
                        break;
                    case ShaderUtil.ShaderPropertyType.TexEnv:
                        if (a.GetTexture(name) != b.GetTexture(name)) return false;
                        if (a.GetTextureOffset(name) != b.GetTextureOffset(name)) return false;
                        if (a.GetTextureScale(name) != b.GetTextureScale(name)) return false;
                        break;
                }
            }
            // 关键字比较。Keyword comparison.
            var ka = new List<string>(a.shaderKeywords ?? new string[0]);
            var kb = new List<string>(b.shaderKeywords ?? new string[0]);
            ka.Sort();
            kb.Sort();
            if (ka.Count != kb.Count) return false;
            for (int i = 0; i < ka.Count; i++)
            {
                if (ka[i] != kb[i]) return false;
            }
            return true;
        }

        // 纹理替换注册：旧贴图只对应一个替换物 → 注册（NDMF 重写动画引用）；多个替换物 → 交由动画重写器按属性处理。
        // Texture replacement registration: 1:1 replacements are registered (NDMF rewrites animation refs);
        // multi-replacement textures are handled property-aware by the animation remapper.
        private static void RegisterTextureReplacements(ATOContext ctx)
        {
            var replacements = new Dictionary<Texture2D, HashSet<Texture2D>>();
            foreach (var e in ctx.islandEntities)
            {
                foreach (var u in e.uses)
                {
                    if (u.texture == null || u.replacementTexture == null) continue;
                    HashSet<Texture2D> set;
                    if (!replacements.TryGetValue(u.texture.source, out set))
                    {
                        set = new HashSet<Texture2D>();
                        replacements[u.texture.source] = set;
                    }
                    set.Add(u.replacementTexture);
                }
            }
            foreach (var entry in ctx.textures)
            {
                if (entry.dedupTarget != null) continue;
                if (entry.replacementTexture != null)
                {
                    HashSet<Texture2D> set;
                    if (!replacements.TryGetValue(entry.source, out set))
                    {
                        set = new HashSet<Texture2D>();
                        replacements[entry.source] = set;
                    }
                    set.Add(entry.replacementTexture);
                }
            }
            foreach (var kv in replacements)
            {
                if (kv.Value.Count == 1)
                {
                    foreach (var r in kv.Value) ctx.ndmf.ObjectRegistry.RegisterReplacedObject(kv.Key, r);
                }
                else if (kv.Value.Count > 1)
                {
                    ATOLog.Debug(string.Format("贴图被多个图集替换，动画引用由属性级重写处理 / texture replaced by multiple atlases, handled property-aware: {0}", kv.Key.name));
                }
            }
        }
    }
}
