// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Output/MaterialPatcher.cs — 材质贴图引用替换 / Material texture reference replacement
//
// 需求: 将图集/缩放贴图重新赋给材质（别忘了动画中的材质）；该过程只修改贴图引用，
//       不修改材质的任何其他属性。
// 实现 (共识):
//  - 绝不原地修改原材质资产：按"引用映射"克隆材质（同一映射的槽位共享一个克隆）。
//  - 白名单贴图保持原引用不动。
//  - 输出绑定级替换表供动画补丁使用。
// ============================================================================
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 材质修补结果 / Material patch results.
    /// </summary>
    public sealed class MaterialPatchResult
    {
        /// <summary>槽位 → 最终材质 / slot → final material</summary>
        public Dictionary<MaterialSlotRef, Material> slotMaterial = new Dictionary<MaterialSlotRef, Material>();

        /// <summary>旧材质 → 新材质（整体克隆映射，供动画材质切换补丁）/
        /// old material → cloned material (for animation material-swap patching)</summary>
        public Dictionary<Material, Material> materialMap = new Dictionary<Material, Material>();

        /// <summary>贴图 → 新贴图（全局；供动画贴图切换补丁）/
        /// texture → new texture (global; for animation texture-swap patching)</summary>
        public Dictionary<Texture2D, Texture2D> textureMap = new Dictionary<Texture2D, Texture2D>();

        /// <summary>绑定级替换: (renderer, slot, property) → new texture /
        /// binding-level replacement map</summary>
        public Dictionary<(Renderer, int, string), Texture2D> bindingTexture =
            new Dictionary<(Renderer, int, string), Texture2D>();
    }

    /// <summary>
    /// 材质修补器 / Material patcher.
    /// </summary>
    public static class MaterialPatcher
    {
        /// <summary>
        /// 执行材质修补 / Patch all materials.
        /// </summary>
        /// <param name="analysis">分析结果 / analysis</param>
        /// <param name="outcome">装箱结果（图集）/ packing outcome (atlases)</param>
        /// <param name="scaledTextures">整图缩放结果（图集关闭/兜底）/ whole-texture scaled results</param>
        public static MaterialPatchResult Patch(AvatarAnalysis analysis, PackOutcome outcome,
            Dictionary<Texture2D, Texture2D> scaledTextures, AnimationData anim)
        {
            var result = new MaterialPatchResult();
            var cloneCache = new Dictionary<string, Material>();

            // 预构建: 贴图源 → 图集（按 (texture, group) 查；槽位 tref 与组内 canonical tref
            // 可能是不同对象，故用 Texture2D 作为键）/
            // precompute: per (texture source, group) the target atlas texture
            // (slot trefs and group trefs may be different instances → key by Texture2D)
            var atlasFor = new Dictionary<(Texture2D, UVGroup), Texture2D>();
            foreach (var family in outcome.families.Values)
            {
                foreach (var atlas in family.atlases)
                {
                    foreach (var kv in atlas.content)
                    {
                        if (kv.Value.Count == 0) continue;
                        atlasFor[(kv.Key.source, kv.Value[0].group)] = atlas.texture;
                    }
                }
            }

            foreach (var slot in analysis.slots)
            {
                if (slot.renderer == null) continue;

                var changes = new Dictionary<string, Texture2D>(); // property → new texture
                foreach (var tref in slot.textures)
                {
                    if (tref.whitelisted || tref.source == null) continue;

                    Texture2D newTex = null;
                    var group = FindGroup(analysis, slot.mesh, tref.uvChannel);
                    if (group != null && group.families.Count > 0 && atlasFor.TryGetValue((tref.source, group), out var atlasTex))
                    {
                        newTex = atlasTex;
                    }
                    else if (scaledTextures != null && scaledTextures.TryGetValue(tref.source, out var scaled))
                    {
                        newTex = scaled;
                    }

                    if (newTex == null || newTex == tref.source) continue;

                    changes[tref.property] = newTex;
                    result.bindingTexture[(slot.renderer, slot.slotIndex, tref.property)] = newTex;
                    result.textureMap[tref.source] = newTex;
                }

                if (changes.Count == 0)
                {
                    result.slotMaterial[slot] = slot.material;
                    continue;
                }

                // 克隆材质（按映射缓存）/ clone material (cached by mapping)
                var key = MappingKey(slot.material, changes);
                if (!cloneCache.TryGetValue(key, out var clone))
                {
                    clone = new Material(slot.material);
                    clone.name = slot.material.name + " (ATO)";
                    clone.hideFlags = HideFlags.HideAndDontSave;
                    foreach (var kv in changes)
                    {
                        clone.SetTexture(kv.Key, kv.Value);
                    }
                    cloneCache[key] = clone;
                    result.materialMap[slot.material] = clone;
                }
                result.slotMaterial[slot] = clone;
            }

            // 动画切换的材质: 也需克隆并替换其贴图引用（材质切换曲线会引用它们）/
            // animation-swapped materials must be cloned with new texture refs too
            if (anim != null)
            {
                foreach (var kv in anim.slotAnims)
                {
                    var r = kv.Key;
                    foreach (var slotInfo in kv.Value)
                    {
                        foreach (var swappedMat in slotInfo.Value.materialSwaps)
                        {
                            if (swappedMat == null || result.materialMap.ContainsKey(swappedMat)) continue;
                            var changes = new Dictionary<string, Texture2D>();
                            foreach (var prop in ShaderAnalyzer.GetTexturePropertyNames(swappedMat))
                            {
                                if (result.bindingTexture.TryGetValue((r, slotInfo.Key, prop), out var newTex))
                                {
                                    changes[prop] = newTex;
                                }
                            }
                            if (changes.Count == 0) continue;
                            var clone = new Material(swappedMat);
                            clone.name = swappedMat.name + " (ATO)";
                            clone.hideFlags = HideFlags.HideAndDontSave;
                            foreach (var pc in changes)
                            {
                                clone.SetTexture(pc.Key, pc.Value);
                            }
                            result.materialMap[swappedMat] = clone;
                        }
                    }
                }
            }

            return result;
        }

        private static UVGroup FindGroup(AvatarAnalysis analysis, Mesh mesh, int channel)
        {
            if (mesh == null || channel < 0) return null;
            if (analysis.groupsByMesh.TryGetValue(mesh, out var map) && map.TryGetValue(channel, out var g))
            {
                return g;
            }
            return null;
        }

        private static string MappingKey(Material mat, Dictionary<string, Texture2D> changes)
        {
            var sb = new StringBuilder();
            sb.Append(mat.GetInstanceID()).Append('|');
            foreach (var kv in changes.OrderBy(k => k.Key))
            {
                sb.Append(kv.Key).Append('=').Append(kv.Value.GetInstanceID()).Append(';');
            }
            return sb.ToString();
        }
    }
}
