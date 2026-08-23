// SPDX-License-Identifier: MIT
// EN: Analyzer for shaders that follow Unity's standard property conventions.
// ZH: 面向遵循 Unity 标准属性约定的着色器的分析器。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Api;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// EN: Handles the Built-in Standard shaders, URP Lit/Unlit, Unity's Unlit family and the VRChat
    ///     mobile fallback shaders. These all sample with UV0 (or UV1 through <c>_UVSec</c>) and honour
    ///     the usual <c>_ST</c> convention, which makes them safe to reason about.
    /// ZH: 处理内置 Standard 着色器、URP Lit/Unlit、Unity 的 Unlit 系列以及 VRChat 移动端回退着色器。
    ///     它们都用 UV0（或通过 <c>_UVSec</c> 用 UV1）采样并遵循常规的 <c>_ST</c> 约定，因此可以安全推理。
    /// </summary>
    public sealed class StandardShaderAnalyzer : IAtoShaderAnalyzer
    {
        /// <inheritdoc/>
        public int Priority => 10;

        private static readonly string[] SupportedPrefixes =
        {
            "Standard",
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Simple Lit",
            "Universal Render Pipeline/Baked Lit",
            "Universal Render Pipeline/Unlit",
            "Unlit/",
            "Mobile/",
            "VRChat/Mobile/",
            "Sprites/",
            "UI/",
        };

        /// <summary>
        /// EN: Properties that Unity's Standard shader samples with the secondary UV set chosen by
        ///     <c>_UVSec</c>.
        /// ZH: Unity Standard 着色器中，使用由 <c>_UVSec</c> 选择的次级 UV 集合采样的属性。
        /// </summary>
        private static readonly HashSet<string> UvSecTextures = new HashSet<string>(StringComparer.Ordinal)
        {
            "_DetailAlbedoMap", "_DetailNormalMap",
        };

        /// <inheritdoc/>
        public bool CanAnalyze(Shader shader)
        {
            if (shader == null) return false;
            foreach (var p in SupportedPrefixes)
                if (shader.name.StartsWith(p, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <inheritdoc/>
        public AtoMaterialAnalysis Analyze(Material material)
        {
            var result = new AtoMaterialAnalysis();
            var shader = material.shader;

            ShaderAnalysisUtil.ResolveAlphaMode(material, out var mode, out var cutoff);
            result.AlphaMode = mode;
            result.Cutoff = cutoff;

            // EN: Parallax mapping displaces the sampling UV per pixel.
            // ZH: 视差映射会逐像素位移采样 UV。
            if (material.HasProperty("_ParallaxMap") && material.GetTexture("_ParallaxMap") != null)
            {
                result.ForceWhitelist = true;
                result.ForceWhitelistReason = "Standard shader parallax map displaces the sampling UV";
                return result;
            }

            int uvSec = Mathf.Clamp(Mathf.RoundToInt(ShaderAnalysisUtil.GetFloat(material, "_UVSec", 0f)), 0, 1);

            foreach (var prop in ShaderAnalysisUtil.GetTextureProperties(shader))
            {
                var tex = material.GetTexture(prop);
                if (tex == null) continue;

                var r = new AtoTextureRef
                {
                    PropertyName = prop,
                    Texture = tex,
                    Kind = ShaderAnalysisUtil.ClassifyKind(shader, prop, tex),
                    IgnoresScaleOffset = (ShaderAnalysisUtil.GetFlags(shader, prop) & UnityEngine.Rendering.ShaderPropertyFlags.NoScaleOffset) != 0,
                    Space = AtoSamplingSpace.NonMeshUV,
                    UvChannel = 0,
                };

                if (ShaderAnalysisUtil.HasIdentityScaleOffset(material, prop))
                {
                    r.Space = AtoSamplingSpace.MeshUV;
                    r.UvChannel = UvSecTextures.Contains(prop) ? uvSec : 0;
                }

                result.Textures.Add(r);
            }

            return result;
        }
    }
}
