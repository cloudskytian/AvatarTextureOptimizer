// ShaderTextureAnalyzer.cs
// Phase 3: Analyzes shader properties (lilToon and others) to classify textures
// and detect which textures are normal maps, masks, etc. Automatically handles
// shader keywords and falls back to content-based detection.
// 阶段3：分析着色器属性（lilToon 等）以分类贴图，检测法线、蒙版等。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;

namespace Fosa.AvatarTextureOptimizer.Core
{
    /// <summary>
    /// Analyzes materials' shaders to determine texture types, usage patterns,
    /// and keyword-driven behavior (e.g., lilToon's feature toggles).
    /// 分析材质着色器以确定贴图类型、使用模式和关键字驱动行为。
    /// </summary>
    internal sealed class ShaderTextureAnalyzer
    {
        private readonly AvatarScanResult _scan;
        private readonly ATOLogger _log;

        // Known shader property classification tables
        // 已知着色器属性分类表
        private static readonly HashSet<string> s_normalMapProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_BumpMap", "_NormalMap", "_NormalTex", "_Bump", "_DetailNormalMap",
            "_MatCapBumpMap", "_MatCap2ndBumpMap", "_Bump2ndMap"
        };

        private static readonly HashSet<string> s_maskProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_MetallicGlossMap", "_OcclusionMap", "_SmoothnessTex", "_RoughnessMap",
            "_DetailMask", "_ShadowBorderMask", "_ShadowBlurMask", "_ShadowStrengthMask",
            "_RimShadeMask", "_EmissionBlendMask", "_OutlineWidthMask",
            "_MainColorAdjustMask", "_AlphaMask", "_DissolveMask"
        };

        private static readonly HashSet<string> s_mainColorProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_MainTex", "_MainColor", "_BaseMap", "_BaseColorMap", "_Albedo",
            "_OutlineTex", "_MatCapTex", "_Main2ndTex", "_Main3rdTex",
            "_ShadowColorTex", "_RimColorTex", "_EmissionColorTex", "_BacklightColorTex",
            "_ReflectionColorTex"
        };

        private static readonly HashSet<string> s_emissionProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_EmissionMap", "_EmissionColorTex", "_Emission2ndMap", "_EmissionGradTex"
        };

        // lilToon UV mode properties that indicate non-standard UV usage
        private static readonly HashSet<string> s_uvModeProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_Main2ndTex_UVMode", "_Main3rdTex_UVMode", "_MatCapTex_UVMode"
        };

        internal ShaderTextureAnalyzer(AvatarScanResult scan, ATOLogger log)
        {
            _scan = scan;
            _log = log;
        }

        internal void Analyze()
        {
            foreach (var kvp in _scan.TextureReferences)
            {
                var tex = kvp.Key;
                var refr = kvp.Value;
                if (tex == null || refr == null) continue;

                // Refine category based on property name
                refr.Category = ClassifyTexture(refr.Material, tex, refr.PropertyName);

                // Check for UV mode keywords that indicate decal/non-standard UV usage
                if (refr.Material != null && HasNonStandardUVUsage(refr.Material))
                {
                    _log.Verbose($"Texture {tex.name} uses non-standard UV mode (decal etc), whitelisting.");
                    _scan.WhitelistedTextures.Add(tex);
                }

                // Check for decal/MSDF flags (lilToon)
                if (refr.Material != null && IsDecalTexture(refr.Material, refr.PropertyName))
                {
                    _log.Verbose($"Texture {tex.name} is a decal, whitelisting.");
                    _scan.WhitelistedTextures.Add(tex);
                }
            }

            // Remove newly-whitelisted textures
            foreach (var wt in _scan.WhitelistedTextures.ToList())
                _scan.TextureReferences.Remove(wt);

            _log.Info($"Shader analysis complete. {_scan.TextureReferences.Count} textures remain after filtering.");
        }

        internal TextureCategory ClassifyTexture(Material mat, Texture2D tex, string propName)
        {
            if (propName != null)
            {
                if (s_normalMapProps.Contains(propName)) return TextureCategory.Normal;
                if (s_maskProps.Contains(propName)) return TextureCategory.Mask;
                if (s_emissionProps.Contains(propName)) return TextureCategory.Emission;
                if (s_mainColorProps.Contains(propName))
                {
                    return HasAlphaChannel(tex) ? TextureCategory.Color : TextureCategory.ColorOpaque;
                }
            }

            // Content-based fallback: check import settings for normal map
            if (tex != null)
            {
                var path = AssetDatabase.GetAssetPath(tex);
                if (!string.IsNullOrEmpty(path))
                {
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer != null && importer.textureType == TextureImporterType.NormalMap)
                        return TextureCategory.Normal;
                }

                // Check if it's a single-channel (grayscale) texture
                if (!GraphicsFormatUtility.HasAlphaChannel(tex.graphicsFormat))
                {
                    // Could be a mask or opaque color
                    if (GraphicsFormatUtility.GetComponentCount(tex.graphicsFormat) == 1)
                        return TextureCategory.Mask;
                    return TextureCategory.ColorOpaque;
                }
                return TextureCategory.Color;
            }

            return TextureCategory.Other;
        }

        private static bool HasAlphaChannel(Texture2D tex)
        {
            if (tex == null) return false;
            return GraphicsFormatUtility.HasAlphaChannel(tex.graphicsFormat);
        }

        /// <summary>
        /// Checks if the material uses non-standard UV modes (lilToon _UVMode, decal, MSDF, etc.)
        /// 检查材质是否使用非标准 UV 模式。
        /// </summary>
        private bool HasNonStandardUVUsage(Material mat)
        {
            if (mat == null) return false;
            foreach (var prop in s_uvModeProps)
            {
                if (mat.HasProperty(prop))
                {
                    var val = mat.GetFloat(prop);
                    // UV modes other than 0 (standard UV0) indicate non-standard usage
                    // 0=UV0, 1=UV1, 2=UV2, 3=UV3, 4=MatCap, 5=Screen, ...
                    if (val > 0)
                    {
                        _log.Verbose($"  Non-standard UV mode: {prop}={val}");
                        return true;
                    }
                }
            }

            // Check for decal keywords
            foreach (var kw in mat.enabledKeywords)
            {
                var kwName = kw.name;
                if (kwName.Contains("DECAL") || kwName.Contains("MSDF") ||
                    kwName.Contains("MATCAP") || kwName.Contains("FUZZ"))
                    return true;
            }

            return false;
        }

        private bool IsDecalTexture(Material mat, string propName)
        {
            if (string.IsNullOrEmpty(propName)) return false;
            // lilToon: _Main2ndTexIsDecal, _Main3rdTexIsDecal
            var decalProp = propName.Replace("Tex", "TexIsDecal");
            if (mat.HasProperty(decalProp) && mat.GetFloat(decalProp) != 0)
                return true;
            // Check _MSDF
            var msdfProp = propName.Replace("Tex", "TexIsMSDF");
            if (mat.HasProperty(msdfProp) && mat.GetFloat(msdfProp) != 0)
                return true;
            return false;
        }

        /// <summary>
        /// Gets the UV channel for a texture property in a material.
        /// Default is 0 (UV0). lilToon supports UV1/UV2/UV3 via _UVMode properties.
        /// 获取贴图属性使用的 UV 通道。
        /// </summary>
        internal static int GetUVChannel(Material mat, string propName)
        {
            if (mat == null || string.IsNullOrEmpty(propName)) return 0;

            // lilToon UV mode
            var uvModeProp = propName + "_UVMode";
            if (mat.HasProperty(uvModeProp))
            {
                var mode = (int)mat.GetFloat(uvModeProp);
                // 0=UV0, 1=UV1, 2=UV2, 3=UV3
                if (mode >= 0 && mode <= 3) return mode;
            }

            return 0;
        }

        /// <summary>
        /// Determines the companion normal/mask maps for a given color texture in a material.
        /// 确定给定主色贴图在材质中的配套法线/蒙版贴图。
        /// </summary>
        internal static (Texture2D normal, Texture2D mask) GetCompanionMaps(Material mat, string mainColorProp)
        {
            Texture2D normal = null;
            Texture2D mask = null;

            if (mat == null) return (null, null);

            int count = ShaderUtil.GetPropertyCount(mat.shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var name = ShaderUtil.GetPropertyName(mat.shader, i);
                var tex = mat.GetTexture(name) as Texture2D;

                if (tex == null) continue;

                // Find normal map paired with this color texture
                if (s_normalMapProps.Contains(name) && mat.HasProperty("_UseBumpMap"))
                {
                    if (mat.GetFloat("_UseBumpMap") != 0)
                        normal = tex;
                }
                else if (name == "_BumpMap" && normal == null)
                {
                    normal = tex;
                }

                // Find mask
                if (s_maskProps.Contains(name))
                    mask = tex;
            }

            return (normal, mask);
        }
    }
}
