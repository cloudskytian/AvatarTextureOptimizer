// AvatarTextureOptimizer
// File: Editor/Analysis/TextureCollector.cs
//
// Scans the avatar: renderers, material slots, mesh UV availability and the
// textures referenced by each material. Produces the initial list of
// TextureUsages. Animation-driven additions are merged later by
// AnimationScanner.
//
// 扫描 Avatar：渲染器、材质槽、网格 UV 可用性以及每个材质引用的贴图。
// 产出初始的 TextureUsage 列表。动画驱动的补充由 AnimationScanner 稍后合并。

using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.editor.logging;
using net.fosa.avatar_texture_optimizer.editor.model;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.analysis
{
    /// <summary>
    /// Collects texture usages from the avatar's renderers.
    /// 从 Avatar 的渲染器收集贴图引用。
    /// </summary>
    public static class TextureCollector
    {
        /// <summary>
        /// Scan all renderers under the avatar root (skipping EditorOnly) and
        /// build the initial usage list.
        /// 扫描 Avatar 根下的所有渲染器（跳过 EditorOnly）并构建初始引用列表。
        /// </summary>
        public static void Scan(GameObject avatarRoot, ATOBuildState state)
        {
            var stopwatch = new ATOStopwatch("TextureCollector.Scan");
            stopwatch.Begin("enumerate renderers");

            var renderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
            int skippedEditorOnly = 0;

            foreach (var renderer in renderers)
            {
                if (renderer.gameObject.CompareTag("EditorOnly"))
                {
                    skippedEditorOnly++;
                    continue;
                }

                // Note: enabled-ness (incl. animation-driven) is filtered in
                // the Group pass via AnimationFacts; here we collect everything
                // so animated renderers are not missed.
                // 注意：启用状态（含动画驱动）在 Group pass 中通过
                // AnimationFacts 过滤；这里收集全部，以免漏掉动画启用的渲染器。

                var materials = renderer.sharedMaterials;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    var material = materials[slot];
                    if (material == null) continue;
                    CollectFromMaterial(renderer, slot, material, state);
                }
            }
            stopwatch.End("enumerate renderers");
            ATOLog.Trace($"collector: skipped EditorOnly={skippedEditorOnly}");
        }

        /// <summary>Collect usages from one material on one slot. / 从一个材质槽收集引用。</summary>
        public static void CollectFromMaterial(Renderer renderer, int slot, Material material, ATOBuildState state)
        {
            var shader = material.shader;
            if (shader == null) return;

            var properties = ShaderAnalyzer.EnumerateTextureProperties(shader);
            foreach (var propertyName in properties)
            {
                if (!material.HasProperty(propertyName)) continue;
                var tex = material.GetTexture(propertyName) as Texture2D;
                if (tex == null) continue;

                // Skip textures that are not asset-backed (e.g. RenderTextures,
                // procedural). We only optimize Texture2D assets.
                // 跳过非资产贴图（如 RenderTexture、程序化贴图）。只优化 Texture2D 资产。
                if (!EditorUtility.IsPersistent(tex)) continue;

                var info = ShaderAnalyzer.AnalyzeProperty(shader, propertyName);

                var usage = new TextureUsage
                {
                    Renderer = renderer,
                    MaterialSlot = slot,
                    Material = material,
                    PropertyName = propertyName,
                    Texture = tex,
                    Type = info.Type,
                    UVChannel = info.UVChannel,
                    FromAnimation = false,
                };

                // Resolve UV channel from the material's UV-mode value if the
                // shader declares one (lilToon "_Xxx_UVMode").
                // 若着色器声明了 UV 模式属性（lilToon "_Xxx_UVMode"），
                // 则从材质的值解析 UV 通道。
                string uvModeProp = propertyName + "_UVMode";
                if (material.HasProperty(uvModeProp))
                {
                    float mode = material.GetFloat(uvModeProp);
                    int resolved = ShaderAnalyzer.ResolveUVModeValue(mode, uvModeProp, out var risky, out var risk);
                    if (risky)
                    {
                        state.Warn($"{usage}: {risk} -> whitelisted / 视作白名单");
                        state.WhitelistedTextures.Add(tex);
                        continue;
                    }
                    usage.UVChannel = resolved;
                }

                if (info.IsRisky)
                {
                    state.Warn($"{usage}: {info.RiskReason} -> whitelisted / 视作白名单");
                    state.WhitelistedTextures.Add(tex);
                    continue;
                }

                // ST transform check: the texture must be sampled without any
                // scale/offset (identity). Animation ST is checked later.
                // ST 变换检查：贴图必须无缩放/偏移（单位值）地被采样。
                // 动画 ST 稍后检查。
                string stProp = propertyName + "_ST";
                if (!info.NoScaleOffset && material.HasProperty(stProp))
                {
                    var st = material.GetVector(stProp);
                    usage.STScale = new Vector2(st.x, st.y);
                    usage.STOffset = new Vector2(st.z, st.w);
                    if (!usage.HasIdentityST)
                    {
                        state.Warn($"{usage}: material ST transform {st} -> whitelisted / 视作白名单");
                        state.WhitelistedTextures.Add(tex);
                        continue;
                    }
                }

                // Texture import metadata for type grouping.
                // 用于类型分组的贴图导入元数据。
                usage.IsSRGB = IsSRGBTexture(tex);
                usage.FilterMode = tex.filterMode;

                // Render mode / cutoff for alpha evaluation.
                // 用于 alpha 评估的渲染模式 / cutoff。
                usage.RenderMode = DetectRenderMode(material);
                if (material.HasProperty("_Cutoff"))
                    usage.Cutoff = material.GetFloat("_Cutoff");

                // A texture can be referenced by multiple materials: keep every
                // usage; strictest requirements are resolved later.
                // 一张贴图可被多个材质引用：保留全部引用；最严苛的需求稍后解析。
                state.AllUsages.Add(usage);
            }
        }

        /// <summary>
        /// Determine the sRGB-ness of a texture import.
        /// 判断贴图导入是否为 sRGB。
        /// </summary>
        public static bool IsSRGBTexture(Texture2D tex)
        {
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return true; // default assumption / 默认假设
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return true;
            // Normal maps are linear; everything else defaults to sRGB.
            // 法线贴图为线性；其他默认 sRGB。
            return importer.textureType != TextureImporterType.NormalMap && !importer.sRGBTexture;
        }

        /// <summary>Detect the transparent render mode keyword of a material. / 检测材质的透明渲染模式关键字。</summary>
        public static string DetectRenderMode(Material material)
        {
            if (material.HasProperty("_Mode")) // Standard shader
            {
                int mode = Mathf.RoundToInt(material.GetFloat("_Mode"));
                switch (mode)
                {
                    case 0: return "Opaque";
                    case 1: return "Cutout";
                    case 2: return "Fade";
                    case 3: return "Transparent";
                }
            }
            if (material.HasProperty("_StencilMode")) return "Unknown";
            // lilToon: shader name / keywords
            // lilToon：着色器名 / 关键字
            if (material.shader != null)
            {
                var name = material.shader.name;
                if (name.Contains("Cutout")) return "Cutout";
                if (name.Contains("Transparent")) return "Transparent";
                if (material.HasProperty("_AlphaMode"))
                {
                    int mode = Mathf.RoundToInt(material.GetFloat("_AlphaMode"));
                    switch (mode)
                    {
                        case 0: return "Opaque";
                        case 1: return "Cutout";
                        case 2: return "Transparent";
                    }
                }
            }
            return "Opaque";
        }

        /// <summary>
        /// Determine which UV channels a mesh actually has.
        /// 确定网格实际具有哪些 UV 通道。
        /// </summary>
        public static List<int> AvailableUVChannels(Mesh mesh)
        {
            var result = new List<int>();
            if (mesh == null) return result;
            for (int c = 0; c < 8; c++)
            {
                try
                {
                    if (mesh.GetUVs(c, new List<Vector2>()).Count > 0) result.Add(c);
                }
                catch
                {
                    // Some channels may not be present. / 部分通道可能不存在。
                }
            }
            return result;
        }
    }
}
