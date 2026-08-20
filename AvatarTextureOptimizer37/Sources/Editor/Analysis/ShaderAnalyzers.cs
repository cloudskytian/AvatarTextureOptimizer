// ============================================================================
// ATO - built-in shader analyzers
// ATO - 内置着色器分析器
//
// 1) LilToonShaderAnalyzer: lilToon 2.x family (property table read from the
//    lilToon 2.3.4 source - see CLAUDE.md item 9).
// 2) StandardShaderAnalyzer: shaders using standard keyword conventions
//    (Unity Standard/Toon/Unlit-like: _MainTex, _BumpMap, _ALPHATEST_ON,
//    _Cutoff, ...). Conservative: anything it cannot confidently classify is
//    treated as special-use (whitelist).
// 3) ShaderAnalysisService: queries built-ins then third-party
//    IATOShaderAnalyzer implementations, caches results per shader.
// ============================================================================

#region

using System.Collections.Generic;
using net.fosa.AvatarTextureOptimizer.Api;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Analysis
{
    public static class ShaderAnalysisService
    {
        private static readonly Dictionary<Shader, ATOShaderAnalysis> _cache = new(ShaderReferenceEqualityComparer.Instance);
        private static readonly List<IATOShaderAnalyzer> _builtIns = new()
        {
            new LilToonShaderAnalyzer(),
            new StandardShaderAnalyzer(),
        };

        /// <summary>Analyzes shader+material. Returns null when no analyzer
        /// understands it (caller whitelists + warns).
        /// 分析着色器+材质；无分析器可理解时返回 null（调用方白名单化+警告）。</summary>
        public static ATOShaderAnalysis Analyze(Shader shader, Material material)
        {
            if (shader == null) return null;
            if (_cache.TryGetValue(shader, out var cached)) return cached;

            ATOShaderAnalysis result = null;
            foreach (var a in _builtIns)
            {
                if (a.TryAnalyze(shader, material, out var r))
                {
                    result = r;
                    break;
                }
            }

            if (result == null)
            {
                foreach (var a in ATOApiRegistry.ShaderAnalyzers)
                {
                    try
                    {
                        if (a.TryAnalyze(shader, material, out var r))
                        {
                            result = r;
                            break;
                        }
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning("[ATO] Custom shader analyzer " + (a?.Tag ?? "?") + " failed: " + e.Message);
                    }
                }
            }

            _cache[shader] = result;
            return result;
        }

        private sealed class ShaderReferenceEqualityComparer : IEqualityComparer<Shader>
        {
            public static readonly ShaderReferenceEqualityComparer Instance = new();
            public int GetHashCode(Shader s) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s);
            public bool Equals(Shader a, Shader b) => a == b;
        }
    }

    // ------------------------------------------------------------------------
    // lilToon 2.x  lilToon 2.x
    // ------------------------------------------------------------------------
    public sealed class LilToonShaderAnalyzer : IATOShaderAnalyzer
    {
        public string Tag => "lilToon";

        private static bool IsLilToon(Shader shader)
        {
            var n = shader.name;
            // "lilToon", "lilToon (Outline)", "lilToon (Gem)", "lilToon (Lite)"...
            // lilToon、lilToon (Outline)、lilToon (Gem)、lilToon (Lite) 等
            return n.StartsWith("lilToon", System.StringComparison.OrdinalIgnoreCase);
        }

        public bool TryAnalyze(Shader shader, Material material, out ATOShaderAnalysis result)
        {
            result = null;
            if (shader == null || !IsLilToon(shader)) return false;

            result = new ATOShaderAnalysis { AnalyzerTag = "lilToon" };

            // Alpha mode: _TransparentMode float (0 opaque, 1 cutout, 2
            // transparent, 4 premultiply). 透明模式：_TransparentMode（0/1/2/4）。
            if (material.HasProperty("_TransparentMode"))
            {
                var mode = (int) material.GetFloat("_TransparentMode");
                switch (mode)
                {
                    case 1: result.AlphaMode = 1; break;
                    case 2: result.AlphaMode = 2; break;
                    case 4: result.AlphaMode = 3; break;
                    default: result.AlphaMode = 0; break;
                }
            }
            // Keyword fallback (lilToon sets UNITY_UI_ALPHACLIP /
            // UNITY_UI_CLIP_RECT). 关键字回退。
            if (result.AlphaMode == 0)
            {
                if (material.IsKeywordEnabled("UNITY_UI_ALPHACLIP")) result.AlphaMode = 1;
                else if (material.IsKeywordEnabled("UNITY_UI_CLIP_RECT")) result.AlphaMode = 2;
            }

            if (material.HasProperty("_Cutoff")) result.CutoffProperty = "_Cutoff";
            if (material.HasProperty("_SubpassCutoff")) result.SubpassCutoffProperty = "_SubpassCutoff";

            // Main texture 主色
            Add(result, "_MainTex", ATOTextureRole.Albedo, 0, null, "_MainTex_ScrollRotate", null);
            // Normal 法线（_UseBumpMap gate）
            Add(result, "_BumpMap", ATOTextureRole.Normal, 0, null, null, "_UseBumpMap");
            // Emission 自发光（_UseEmission gate; UVMode 4 = Rim = special）
            Add(result, "_EmissionMap", ATOTextureRole.Emission, 0, "_EmissionMap_UVMode", "_EmissionMap_ScrollRotate", "_UseEmission");
            // 2nd main 次色（_UseMain2ndTex gate; UVMode 4 = MatCap = special）
            Add(result, "_Main2ndTex", ATOTextureRole.Albedo, 0, "_Main2ndTex_UVMode", "_Main2ndTex_ScrollRotate", "_UseMain2ndTex");
            // 3rd main 三色
            Add(result, "_Main3rdTex", ATOTextureRole.Albedo, 0, "_Main3rdTex_UVMode", "_Main3rdTex_ScrollRotate", "_UseMain3rdTex");
            // 2nd normal 次法线
            Add(result, "_Bump2ndMap", ATOTextureRole.Normal, 0, null, null, "_UseBump2ndMap");
            // Alpha mask 蒙版（_AlphaMaskMode gate）
            Add(result, "_MaskMap", ATOTextureRole.Mask, 0, null, null, "_AlphaMaskMode");
            // Special-use utilities (always whitelisted) 特殊用途（始终白名单）
            Add(result, "_MainGradationTex", ATOTextureRole.Utility, 0, null, null, null, special: true, noST: true);
            Add(result, "_MainColorAdjustMask", ATOTextureRole.Utility, 0, null, null, null, special: true, noST: true);
            Add(result, "_DitherTex", ATOTextureRole.Utility, 0, null, null, null, special: true, noST: true);

            return true;
        }

        private static void Add(ATOShaderAnalysis r, string prop, ATOTextureRole role, int uv,
            string uvModeProp, string scrollProp, string enableProp, bool special = false, bool noST = false)
        {
            r.Textures.Add(new ATOShaderTextureRef
            {
                Property = prop,
                Role = role,
                UVChannel = uv,
                UVModeProperty = uvModeProp,
                ScrollRotateProperty = scrollProp,
                EnableProperty = enableProp,
                NoScaleOffset = noST,
                SpecialUse = special,
            });
        }
    }

    // ------------------------------------------------------------------------
    // Standard keyword shaders 标准关键字着色器
    // ------------------------------------------------------------------------
    public sealed class StandardShaderAnalyzer : IATOShaderAnalyzer
    {
        public string Tag => "standard";

        // property -> (role, channel)  属性 -> (角色, 通道)
        private static readonly (string prop, ATOTextureRole role, int uv)[] Table =
        {
            ("_MainTex", ATOTextureRole.Albedo, 0),
            ("_BaseMap", ATOTextureRole.Albedo, 0),
            ("_BumpMap", ATOTextureRole.Normal, 0),
            ("_NormalMap", ATOTextureRole.Normal, 0),
            ("_MetallicGlossMap", ATOTextureRole.Mask, 0),
            ("_MetallicSpecGlossMap", ATOTextureRole.Mask, 0),
            ("_SpecGlossMap", ATOTextureRole.Mask, 0),
            ("_OcclusionMap", ATOTextureRole.Mask, 0),
            ("_OcclusionTexture", ATOTextureRole.Mask, 0),
            ("_EmissionMap", ATOTextureRole.Emission, 0),
            ("_EmissiveColorMap", ATOTextureRole.Emission, 0),
            ("_DetailMask", ATOTextureRole.Mask, 0),
            ("_DetailAlbedoMap", ATOTextureRole.Albedo, 0),
            ("_DetailNormalMap", ATOTextureRole.Normal, 0),
        };

        public bool TryAnalyze(Shader shader, Material material, out ATOShaderAnalysis result)
        {
            result = null;
            if (shader == null) return false;

            // Only proceed when the shader actually exposes at least one known
            // texture property; otherwise leave it to other analyzers.
            // 仅当着色器确实暴露至少一个已知贴图属性时才继续。
            bool any = false;
            foreach (var (prop, _, _) in Table)
            {
                if (shader.FindPropertyIndex(prop) >= 0) { any = true; break; }
            }
            if (!any) return false;

            result = new ATOShaderAnalysis { AnalyzerTag = "standard" };

            // Alpha mode keywords 透明模式关键字
            if (material.IsKeywordEnabled("_ALPHATEST_ON")) result.AlphaMode = 1;
            else if (material.IsKeywordEnabled("_ALPHABLEND_ON")) result.AlphaMode = 2;
            else if (material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON")) result.AlphaMode = 3;
            if (material.HasProperty("_Cutoff")) result.CutoffProperty = "_Cutoff";

            foreach (var (prop, role, uv) in Table)
            {
                if (shader.FindPropertyIndex(prop) < 0) continue;
                result.Textures.Add(new ATOShaderTextureRef
                {
                    Property = prop,
                    Role = role,
                    UVChannel = uv,
                });
            }
            return true;
        }
    }
}
