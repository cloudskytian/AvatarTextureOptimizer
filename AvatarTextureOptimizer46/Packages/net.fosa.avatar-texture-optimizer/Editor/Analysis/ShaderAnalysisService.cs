// SPDX-License-Identifier: MIT
// EN: Dispatches material analysis to the registered analyzers and caches the result.
// ZH: 将材质分析派发到已注册的分析器，并缓存结果。

using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Api;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// EN: Entry point for "what does this material do with its textures". Results are cached per
    ///     material instance for the duration of one build.
    /// ZH: “这个材质如何使用它的贴图”的统一入口。结果在单次构建期间按材质实例缓存。
    /// </summary>
    public sealed class ShaderAnalysisService
    {
        private readonly Dictionary<Material, AtoMaterialAnalysis> _cache = new Dictionary<Material, AtoMaterialAnalysis>();

        [InitializeOnLoadMethod]
        private static void RegisterBuiltins()
        {
            AtoShaderAnalyzerRegistry.Register(new LilToonShaderAnalyzer());
            AtoShaderAnalyzerRegistry.Register(new StandardShaderAnalyzer());
        }

        /// <summary>
        /// EN: Analyzes a material. When no analyzer recognizes the shader the material is reported as
        ///     force whitelisted, which is the safe default demanded by the specification.
        /// ZH: 分析一个材质。若没有分析器认识该着色器，则将材质报告为强制白名单，
        ///     这是规格要求的安全默认行为。
        /// </summary>
        public AtoMaterialAnalysis Analyze(Material material)
        {
            if (material == null) return null;
            if (_cache.TryGetValue(material, out var cached)) return cached;

            AtoMaterialAnalysis result = null;
            foreach (var analyzer in AtoShaderAnalyzerRegistry.Analyzers)
            {
                if (!analyzer.CanAnalyze(material.shader)) continue;
                result = analyzer.Analyze(material);
                if (result != null) break;
            }

            if (result == null)
            {
                result = new AtoMaterialAnalysis
                {
                    ForceWhitelist = true,
                    ForceWhitelistReason = $"no analyzer for shader '{(material.shader != null ? material.shader.name : "<null>")}'",
                };
                AtoLog.Debug_("Analyze", $"Material '{material.name}' uses an unsupported shader; treated as whitelisted.");
            }

            _cache[material] = result;
            return result;
        }

        /// <summary>EN: Clears the cache. ZH: 清空缓存。</summary>
        public void Clear() => _cache.Clear();
    }
}
