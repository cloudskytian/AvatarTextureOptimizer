// Shader semantics analysis: which texture properties are mesh-UV sampled, their role,
// UV channel, transform-safety and alpha mode. Unknown => whitelist (safe fallback).
// 着色器语义分析：贴图属性的采样方式/角色/UV通道/变换安全性/透明模式；未知一律白名单兜底。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato.editor
{
    /// <summary>Analysis of one texture property on a material. / 单个贴图属性的分析结果。</summary>
    public class PropSemantics
    {
        public string Property;
        public TexRole Role;
        public int UvChannel;          // -1 = not mesh-UV sampled (matcap/screen) / 非网格UV采样
        public byte UsedChannels = 0xF;
        public bool Safe = true;       // no ST/scroll/decal transforms / 无任何变换
        public string UnsafeReason;
    }

    /// <summary>Whole-material analysis. / 整材质分析结果。</summary>
    public class MaterialSemantics
    {
        public bool Supported;          // shader recognized / 着色器可识别
        public string UnsupportedReason;
        public AlphaMode Alpha = AlphaMode.Opaque;
        public float Cutoff = 0.5f;
        public readonly List<PropSemantics> Props = new List<PropSemantics>();
    }

    /// <summary>Extension point for third-party shader support. / 第三方着色器扩展点。</summary>
    public interface IAtoShaderSemanticsProvider
    {
        int Priority { get; }
        bool CanHandle(Shader shader);
        MaterialSemantics Analyze(Material material);
    }

    public static class ShaderSemantics
    {
        private static readonly List<IAtoShaderSemanticsProvider> _providers = new List<IAtoShaderSemanticsProvider>();
        private static bool _initialized;

        /// <summary>Register a custom provider (advanced users / 3rd parties). / 注册自定义 Provider。</summary>
        public static void Register(IAtoShaderSemanticsProvider provider)
        {
            _providers.Add(provider);
            _providers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        private static void EnsureInit()
        {
            if (_initialized) return;
            _initialized = true;
            Register(new LiltoonSemanticsProvider());
            Register(new StandardKeywordSemanticsProvider());
        }

        public static MaterialSemantics Analyze(Material mat)
        {
            EnsureInit();
            if (mat == null || mat.shader == null)
                return new MaterialSemantics { Supported = false, UnsupportedReason = "null material/shader" };
            foreach (var p in _providers)
            {
                if (!p.CanHandle(mat.shader)) continue;
                try { return p.Analyze(mat); }
                catch (Exception e)
                {
                    AtoLog.Warn($"semantics provider {p.GetType().Name} failed on '{mat.name}': {e.Message}");
                }
            }
            return new MaterialSemantics { Supported = false, UnsupportedReason = $"unknown shader '{mat.shader.name}'" };
        }
    }

    /// <summary>
    /// lilToon analyzer. Property table derived from lilToon 2.3.4 sources (lts*.shader /
    /// lil_common_input.hlsl) cross-checked with AAO's ShaderInformation.Liltoon.cs. Newer
    /// lilToon versions are detected via lilToon.lilConstants.currentVersionValue reflection;
    /// unknown texture properties fall back to whitelist per-texture.
    /// lilToon 分析器：属性表取自 lilToon 2.3.4 源码并与 AAO 语义表交叉核对；
    /// 通过反射 lilConstants 检测版本，未知属性逐贴图白名单兜底。
    /// </summary>
    internal class LiltoonSemanticsProvider : IAtoShaderSemanticsProvider
    {
        public int Priority => 100;
        private const int MaxKnownLiltoonVersion = 45; // lilToon 2.3.x currentVersionValue

        // property -> (role, usedChannels). uv resolved separately. / 属性语义表
        private static readonly Dictionary<string, (TexRole role, byte ch)> KnownUvMain = new()
        {
            { "_MainTex", (TexRole.Color, 0xF) },
            { "_MainColorAdjustMask", (TexRole.Gray, 0x7) },
            { "_AlphaMask", (TexRole.Gray, 0x1) },
            { "_BumpMap", (TexRole.Normal, 0xF) },
            { "_Bump2ndScaleMask", (TexRole.Gray, 0x1) },
            { "_AnisotropyTangentMap", (TexRole.Normal, 0xF) },
            { "_AnisotropyScaleMask", (TexRole.Gray, 0x1) },
            { "_AnisotropyShiftNoiseMask", (TexRole.Gray, 0x1) },
            { "_BacklightColorTex", (TexRole.Color, 0xF) },
            { "_ShadowStrengthMask", (TexRole.Gray, 0x1) },
            { "_ShadowBorderMask", (TexRole.Gray, 0x7) },
            { "_ShadowBlurMask", (TexRole.Gray, 0x7) },
            { "_ShadowColorTex", (TexRole.Color, 0xF) },
            { "_Shadow2ndColorTex", (TexRole.Color, 0xF) },
            { "_Shadow3rdColorTex", (TexRole.Color, 0xF) },
            { "_RimShadeMask", (TexRole.Gray, 0x1) },
            { "_SmoothnessTex", (TexRole.Gray, 0x1) },
            { "_MetallicGlossMap", (TexRole.Gray, 0x1) },
            { "_ReflectionColorTex", (TexRole.Color, 0xF) },
            { "_MatCapBlendMask", (TexRole.Gray, 0x1) },
            { "_MatCap2ndBlendMask", (TexRole.Gray, 0x1) },
            { "_MatCapBumpMap", (TexRole.Normal, 0xF) },
            { "_MatCap2ndBumpMap", (TexRole.Normal, 0xF) },
            { "_RimColorTex", (TexRole.Color, 0xF) },
            { "_GlitterColorTex", (TexRole.Color, 0xF) },
            { "_Main2ndBlendMask", (TexRole.Gray, 0x1) },
            { "_Main3rdBlendMask", (TexRole.Gray, 0x1) },
            { "_EmissionBlendMask", (TexRole.Gray, 0xF) },
            { "_Emission2ndBlendMask", (TexRole.Gray, 0xF) },
            { "_OutlineTex", (TexRole.Color, 0xF) },
            { "_OutlineWidthMask", (TexRole.Gray, 0x1) },
            { "_FurMask", (TexRole.Gray, 0x1) },
            { "_FurLengthMask", (TexRole.Gray, 0x1) },
            { "_FurVectorTex", (TexRole.Normal, 0xF) },
        };

        // Properties with dedicated UV-mode selectors. / 带 UV 模式选择器的属性。
        private static readonly Dictionary<string, string> UvModeProps = new()
        {
            { "_Main2ndTex", "_Main2ndTex_UVMode" },
            { "_Main3rdTex", "_Main2ndTex_UVMode" }, // liltoon source uses Main2nd mode for 3rd too (verified in AAO table)
            { "_Bump2ndMap", "_Bump2ndMap_UVMode" },
            { "_EmissionMap", "_EmissionMap_UVMode" },
            { "_Emission2ndMap", "_Emission2ndMap_UVMode" },
        };

        private static readonly Dictionary<string, TexRole> UvModeRoles = new()
        {
            { "_Main2ndTex", TexRole.Color },
            { "_Main3rdTex", TexRole.Color },
            { "_Bump2ndMap", TexRole.Normal },
            { "_EmissionMap", TexRole.Color },
            { "_Emission2ndMap", TexRole.Color },
        };

        // Never mesh-UV sampled: safe to ignore entirely (not whitelisted, simply untouched).
        // 非网格UV采样：直接忽略（保持原样，不参与优化也不必白名单整组）。
        private static readonly HashSet<string> NonMeshUv = new()
        {
            "_MatCapTex", "_MatCap2ndTex", "_DitherTex", "_MainGradationTex",
            "_EmissionGradTex", "_Emission2ndGradTex", "_ParallaxMap", "_TriMask", "_FurNoiseMask",
        };

        public bool CanHandle(Shader shader) =>
            shader != null && (shader.name.Contains("lilToon") || shader.name.StartsWith("_lil/") ||
                               shader.name.Contains("ltspass"));

        private static bool VersionSupported()
        {
            // Reflect lilToon.lilConstants.currentVersionValue; if newer than what we know,
            // still analyze but unknown props are per-texture whitelisted anyway.
            // 反射版本值；更新版本仍分析，未知属性已有白名单兜底。
            try
            {
                var t = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("lilToon.lilConstants")).FirstOrDefault(x => x != null);
                if (t == null) return true;
                var f = t.GetField("currentVersionValue", BindingFlags.Public | BindingFlags.Static);
                if (f?.GetValue(null) is int v && v > MaxKnownLiltoonVersion)
                {
                    AtoLog.Warn($"lilToon version value {v} is newer than the known {MaxKnownLiltoonVersion}; " +
                                "unknown texture properties will be whitelisted.");
                }
                return true;
            }
            catch { return true; }
        }

        public MaterialSemantics Analyze(Material mat)
        {
            VersionSupported();
            var result = new MaterialSemantics { Supported = true };
            var name = mat.shader.name;

            // Alpha mode: shader-name based (liltoon variants) + Multi's _TransparentMode.
            // 透明模式：按变体名称 + Multi 的 _TransparentMode。
            if (name.Contains("Cutout")) result.Alpha = AlphaMode.Cutout;
            else if (name.Contains("Transparent") || name.Contains("Trans") || name.Contains("Overlay") ||
                     name.Contains("Fur") || name.Contains("Refraction") || name.Contains("Gem"))
                result.Alpha = AlphaMode.Blend;
            if (mat.HasProperty("_TransparentMode"))
            {
                var tm = Mathf.RoundToInt(mat.GetFloat("_TransparentMode"));
                if (tm == 1) result.Alpha = AlphaMode.Cutout;
                else if (tm >= 2) result.Alpha = AlphaMode.Blend;
            }
            if (mat.HasProperty("_Cutoff")) result.Cutoff = mat.GetFloat("_Cutoff");

            bool mainUvSafe = MainUvIsSafe(mat, out var mainUnsafeReason);

            var shader = mat.shader;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                var prop = shader.GetPropertyName(i);
                var tex = mat.GetTexture(prop) as Texture2D;
                if (tex == null) continue;
                if (NonMeshUv.Contains(prop)) continue; // untouched / 保持原样

                var ps = new PropSemantics { Property = prop };
                if (KnownUvMain.TryGetValue(prop, out var known))
                {
                    ps.Role = known.role;
                    ps.UsedChannels = known.ch;
                    ps.UvChannel = 0;
                    if (!mainUvSafe) { ps.Safe = false; ps.UnsafeReason = mainUnsafeReason; }
                }
                else if (UvModeRoles.TryGetValue(prop, out var role))
                {
                    ps.Role = role;
                    int mode = mat.HasProperty(UvModeProps[prop])
                        ? Mathf.RoundToInt(mat.GetFloat(UvModeProps[prop])) : 0;
                    if (mode <= 3) ps.UvChannel = mode == 0 ? 0 : mode;
                    else { ps.UvChannel = -1; ps.Safe = false; ps.UnsafeReason = $"{prop} uses non-mesh UV mode {mode}"; }
                    if (ps.UvChannel == 0 && !mainUvSafe) { ps.Safe = false; ps.UnsafeReason = mainUnsafeReason; }
                    if (!SubTexIsSafe(mat, prop, out var subReason)) { ps.Safe = false; ps.UnsafeReason = subReason; }
                }
                else
                {
                    // Unknown texture property (future liltoon etc.) -> whitelist this texture.
                    // 未知贴图属性（未来版本等）→ 该贴图白名单。
                    ps.Role = TexRole.Color;
                    ps.Safe = false;
                    ps.UnsafeReason = $"unknown liltoon texture property '{prop}'";
                }

                // per-property _ST check / 每属性 ST 检查
                if (ps.Safe && !StIsIdentity(mat, prop))
                {
                    ps.Safe = false;
                    ps.UnsafeReason = $"{prop}_ST is not identity";
                }
                result.Props.Add(ps);
            }
            return result;
        }

        private static bool MainUvIsSafe(Material mat, out string reason)
        {
            reason = null;
            if (!StIsIdentity(mat, "_MainTex")) { reason = "_MainTex_ST is not identity"; return false; }
            if (mat.HasProperty("_MainTex_ScrollRotate") && mat.GetVector("_MainTex_ScrollRotate") != Vector4.zero)
            { reason = "_MainTex_ScrollRotate active"; return false; }
            if (mat.HasProperty("_ShiftBackfaceUV") && mat.GetFloat("_ShiftBackfaceUV") != 0)
            { reason = "_ShiftBackfaceUV active"; return false; }
            return true;
        }

        private static bool SubTexIsSafe(Material mat, string prop, out string reason)
        {
            reason = null;
            foreach (var suffix in new[] { "Angle", "_ScrollRotate" })
            {
                var p = prop + suffix;
                if (mat.HasProperty(p))
                {
                    if (suffix == "Angle" && Mathf.Abs(mat.GetFloat(p)) > 1e-5f)
                    { reason = $"{p} != 0"; return false; }
                    if (suffix == "_ScrollRotate" && mat.GetVector(p) != Vector4.zero)
                    { reason = $"{p} active"; return false; }
                }
            }
            var decal = prop + "IsDecal";
            if (mat.HasProperty(decal) && mat.GetFloat(decal) != 0) { reason = $"{decal} active (decal use)"; return false; }
            var anim = prop + "DecalAnimation";
            if (mat.HasProperty(anim) && mat.GetVector(anim) != new Vector4(1, 1, 1, 30))
            { reason = $"{anim} active"; return false; }
            return true;
        }

        internal static bool StIsIdentity(Material mat, string prop)
        {
            if (!mat.HasProperty(prop)) return true;
            var s = mat.GetTextureScale(prop);
            var o = mat.GetTextureOffset(prop);
            return (s - Vector2.one).sqrMagnitude < 1e-10f && o.sqrMagnitude < 1e-10f;
        }
    }

    /// <summary>
    /// Generic analyzer for shaders using standard Unity property names / keywords
    /// (Standard, VRChat mobile shaders, many toon shaders). Unknown props => whitelist.
    /// 标准关键字着色器的通用分析器；未知属性白名单。
    /// </summary>
    internal class StandardKeywordSemanticsProvider : IAtoShaderSemanticsProvider
    {
        public int Priority => 0;

        private static readonly Dictionary<string, (TexRole role, byte ch)> Known = new()
        {
            { "_MainTex", (TexRole.Color, 0xF) },
            { "_BaseMap", (TexRole.Color, 0xF) },
            { "_BaseColorMap", (TexRole.Color, 0xF) },
            { "_BumpMap", (TexRole.Normal, 0xF) },
            { "_DetailNormalMap", (TexRole.Normal, 0xF) },
            { "_MetallicGlossMap", (TexRole.Gray, 0xF) },
            { "_SpecGlossMap", (TexRole.Gray, 0xF) },
            { "_OcclusionMap", (TexRole.Gray, 0x2) }, // Standard reads G / Standard 读 G 通道
            { "_EmissionMap", (TexRole.Color, 0xF) },
            { "_ParallaxMap", (TexRole.Gray, 0x1) },
        };

        public bool CanHandle(Shader shader) => shader != null;

        public MaterialSemantics Analyze(Material mat)
        {
            var result = new MaterialSemantics { Supported = true };
            var shader = mat.shader;

            if (mat.IsKeywordEnabled("_ALPHATEST_ON")) result.Alpha = AlphaMode.Cutout;
            else if (mat.IsKeywordEnabled("_ALPHABLEND_ON") || mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
                result.Alpha = AlphaMode.Blend;
            else if (mat.renderQueue >= 2450 && mat.renderQueue < 3000) result.Alpha = AlphaMode.Cutout;
            else if (mat.renderQueue >= 3000) result.Alpha = AlphaMode.Blend;
            if (mat.HasProperty("_Cutoff")) result.Cutoff = mat.GetFloat("_Cutoff");

            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                var prop = shader.GetPropertyName(i);
                var tex = mat.GetTexture(prop) as Texture2D;
                if (tex == null) continue;

                var ps = new PropSemantics { Property = prop, UvChannel = 0 };
                var flags = shader.GetPropertyFlags(i);
                if (Known.TryGetValue(prop, out var known)) { ps.Role = known.role; ps.UsedChannels = known.ch; }
                else if ((flags & ShaderPropertyFlags.Normal) != 0) ps.Role = TexRole.Normal;
                else if ((flags & ShaderPropertyFlags.MainTexture) != 0) ps.Role = TexRole.Color;
                else
                {
                    ps.Safe = false;
                    ps.UnsafeReason = $"unknown texture property '{prop}' on shader '{shader.name}'";
                }

                if (ps.Safe && !LiltoonSemanticsProvider.StIsIdentity(mat, prop))
                {
                    ps.Safe = false;
                    ps.UnsafeReason = $"{prop}_ST is not identity";
                }
                result.Props.Add(ps);
            }
            return result;
        }
    }
}
