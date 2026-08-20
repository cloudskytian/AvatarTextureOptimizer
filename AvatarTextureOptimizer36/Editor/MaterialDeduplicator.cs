using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Deduplicates identical generated materials without touching animated slot topology. / 在不触碰动画槽拓扑的前提下去重完全相同的生成材质。
    /// </summary>
    internal static class MaterialDeduplicator
    {
        public static void Apply(BuildSnapshot snapshot, ATOBuildSession.BuildContextAdapter context,
            AvatarTextureOptimizer component, ATOLogger logger, ATOBuildReport report)
        {
            Dictionary<string, Material> canonical = new Dictionary<string, Material>();
            for (int rendererIndex = 0; rendererIndex < snapshot.Renderers.Count; rendererIndex++)
            {
                RendererRecord renderer = snapshot.Renderers[rendererIndex];
                if (renderer.Renderer == null) continue;
                RendererAnimationInfo animationInfo;
                bool animatedSlots = snapshot.AnimationInfo.TryGetValue(renderer.Renderer, out animationInfo) &&
                                      animationInfo.HasAnimatedMaterialSwitch;
                Material[] materials = renderer.Renderer.sharedMaterials;
                bool changed = false;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    Material material = materials[slot];
                    if (material == null) continue;
                    MaterialUse currentUse = FindUse(renderer, slot);
                    if (component.IsWhitelisted(material) || (currentUse != null && currentUse.SkipAll)) continue;
                    string key = Signature(material);
                    Material existing;
                    if (canonical.TryGetValue(key, out existing) && existing != null)
                    {
                        if (!animatedSlots)
                        {
                            materials[slot] = existing;
                            MaterialUse use = FindUse(renderer, slot);
                            if (use != null) use.WorkingMaterial = existing;
                            report.DeduplicatedMaterials++;
                            changed = true;
                        }
                    }
                    else canonical[key] = material;
                }
                if (changed) renderer.Renderer.sharedMaterials = materials;
            }
            if (report.DeduplicatedMaterials > 0)
                logger.Info("Material deduplication removed " + report.DeduplicatedMaterials + " duplicate(s). / 材质去重移除了重复项。");
        }

        private static MaterialUse FindUse(RendererRecord renderer, int slot)
        {
            for (int i = 0; i < renderer.Materials.Count; i++) if (renderer.Materials[i].Slot == slot) return renderer.Materials[i];
            return null;
        }

        private static string Signature(Material material)
        {
            string json;
            try
            {
                json = EditorJsonUtility.ToJson(material, false);
            }
            catch (Exception)
            {
                json = material.shader == null ? string.Empty : material.shader.name;
            }
            // Names are identity labels, not material content. / 名称是标识，不属于材质内容。
            json = Regex.Replace(json, "\\\"m_Name\\\"\\s*:\\s*\\\"(?:\\\\.|[^\\\"])*\\\"", "");
            string[] keywords = material.shaderKeywords;
            Array.Sort(keywords, StringComparer.Ordinal);
            return (material.shader == null ? string.Empty : material.shader.name) + "|" + material.renderQueue + "|" +
                   string.Join(",", keywords) + "|" + json;
        }
    }
}
