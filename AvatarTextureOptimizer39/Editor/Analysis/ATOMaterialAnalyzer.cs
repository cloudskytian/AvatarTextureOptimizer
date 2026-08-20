// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using AvatarTextureOptimizer.Editor.ShaderAnalysis;
using UnityEditor;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// Analyzes a single material's shader and produces the list of eligible texture
    /// bindings (property → texture, semantic category, UV channel), applying the safety
    /// rules: no ST scale/offset/rotation transform (including animated ones), no decal
    /// or other special usage. Any unsafe usage whitelists the texture.
    ///
    /// 分析单个材质的着色器，产出符合条件的贴图绑定列表（属性→贴图、语义类别、UV 通道），
    /// 并应用安全规则：无 ST 平移/缩放/旋转（含动画修改）、无贴花等特殊用途。
    /// 任何不安全用法将贴图列入白名单。
    /// </summary>
    public sealed class ATOMaterialAnalyzer
    {
        private readonly ATOAnimationQueries _anim;
        private readonly ATOWhitelist _whitelist;
        private readonly string _rendererPath;

        public ATOMaterialAnalyzer(ATOAnimationQueries anim, ATOWhitelist whitelist, string rendererPath)
        {
            _anim = anim;
            _whitelist = whitelist;
            _rendererPath = rendererPath;
        }

        /// <summary>
        /// Analyze a material. Returns bindings plus a set of textures that must be skipped.
        /// 分析材质。返回绑定列表与必须跳过的贴图集合。
        /// </summary>
        public List<ATOTextureBinding> Analyze(Material mat, HashSet<Texture2D> skipped)
        {
            var result = new List<ATOTextureBinding>();
            if (mat == null || mat.shader == null) return result;

            var shaderInfo = ATOShaderAnalyzerRegistry.Analyze(mat.shader);

            if (shaderInfo.Unsupported)
            {
                ATOLog.Warning($"Shader {mat.shader.name} unsupported ({shaderInfo.UnsupportedReason}); " +
                               $"whitelisting its textures. / 着色器不支持，其贴图列入白名单。");
                foreach (var t in EnumerateTextures(mat))
                    skipped.Add(t);
                return result;
            }

            // Resolve per-property UV channels. 解析每个属性的 UV 通道。
            var uvChannels = ATOUVChannelAnalyzer.ResolveChannels(mat.shader);

            foreach (var texProp in shaderInfo.Textures)
            {
                var texture = mat.GetTexture(texProp.PropertyName) as Texture2D;
                if (texture == null) continue;

                int uvChannel = uvChannels.TryGetValue(texProp.PropertyName, out var ch) ? ch : 0;

                // Whitelisted → skip (but still record binding category for UV-set grouping later).
                // 白名单 → 跳过（但仍记录绑定类别供 UV 组分组）。
                if (_whitelist.ContainsTexture(texture))
                {
                    skipped.Add(texture);
                    result.Add(new ATOTextureBinding
                    {
                        PropertyName = texProp.PropertyName,
                        Texture = texture,
                        Category = ToCategory(texProp.Semantic),
                        UVChannel = uvChannel,
                    });
                    continue;
                }

                // Safety: ST transform. 安全性：ST 变换。
                if (!IsTransformSafe(mat, texProp))
                {
                    ATOLog.Verbose($"Texture {texture.name} has a non-identity or animated ST transform; " +
                                   $"whitelisted. / 贴图存在非单位或动画驱动的 ST 变换，列入白名单。");
                    skipped.Add(texture);
                    continue;
                }

                result.Add(new ATOTextureBinding
                {
                    PropertyName = texProp.PropertyName,
                    Texture = texture,
                    Category = ToCategory(texProp.Semantic),
                    UVChannel = uvChannel,
                });
            }

            return result;
        }

        private static ATOTextureCategory ToCategory(ATOTextureSemantic s)
        {
            switch (s)
            {
                case ATOTextureSemantic.Normal: return ATOTextureCategory.Normal;
                case ATOTextureSemantic.Mask:
                case ATOTextureSemantic.MetallicGloss: return ATOTextureCategory.Mask;
                case ATOTextureSemantic.Emission: return ATOTextureCategory.Emission;
                case ATOTextureSemantic.MatCap: return ATOTextureCategory.Mask;
                default: return ATOTextureCategory.Albedo;
            }
        }

        /// <summary>Check ST/scroll-rotate transform safety. 检查 ST/scroll-rotate 变换安全性。</summary>
        private bool IsTransformSafe(Material mat, ATOShaderTextureInfo prop)
        {
            if (prop.NoScaleOffset || prop.TransformProperties.Count == 0) return true;

            foreach (var tp in prop.TransformProperties)
            {
                if (!mat.HasProperty(tp)) continue;

                var v = mat.GetVector(tp);
                if (!IsIdentity(tp, v)) return false;

                // Check animation. 检查动画。
                if (_anim.IsMaterialPropertyAnimated(_rendererPath, tp) ||
                    _anim.IsMaterialPropertyAnimated(_rendererPath, "material." + tp))
                    return false;
            }

            return true;
        }

        private static bool IsIdentity(string propName, Vector4 v)
        {
            if (propName.Contains("ScrollRotate") || propName.Contains("_Pan") ||
                propName.Contains("_Rot") || propName.Contains("_Angle"))
                return v == Vector4.zero;

            // Standard _ST: scale=(x,y), offset=(z,w). Identity = (1,1,0,0).
            return Mathf.Approximately(v.x, 1f) && Mathf.Approximately(v.y, 1f) &&
                   Mathf.Approximately(v.z, 0f) && Mathf.Approximately(v.w, 0f);
        }

        private static IEnumerable<Texture2D> EnumerateTextures(Material mat)
        {
            foreach (var name in mat.GetTexturePropertyNames())
            {
                var t = mat.GetTexture(name) as Texture2D;
                if (t != null) yield return t;
            }
        }
    }
}
