// Avatar Texture Optimizer / 头像贴图优化器
// Baked shader knowledge base (lilToon 2.3.4 property table + standard-like
// keyword rules). The table is a fingerprint of lilToon's editor property
// list (Editor/lilInspector/lilMaterialProperties.cs); at run time every entry
// is validated against the actual material (HasProperty) so future lilToon
// versions degrade gracefully: unknown texture properties are treated as
// "cannot prove safe" and their textures act as whitelist.
// 烘焙着色器知识库（lilToon 2.3.4 属性表 + 标准关键字规则）。该表是 lilToon
// 编辑器属性列表的指纹；运行时逐条以 HasProperty 校验，因此未来版本出现未知
// 贴图属性时会按"无法证明安全"处理：对应贴图按白名单处理。
//
// Extraction verified against lilToon 2.3.4 on 2026-08-19 (see CLAUDE.md).

using System.Collections.Generic;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>How lilToon-ness is recognized. / lilToon 判定方式。</summary>
    public enum ATOShaderFamily
    {
        Unknown = 0,
        LilToon = 1,
        StandardLike = 2,
    }

    /// <summary>One baked property rule. / 一条烘焙属性规则。</summary>
    public readonly struct ATOPropRule
    {
        /// <summary>Texture property name. / 贴图属性名。</summary>
        public readonly string name;
        /// <summary>Role. / 角色。</summary>
        public readonly ATORole role;
        /// <summary>Channels consumed by shader (bit0=R). / 着色器消费的通道。</summary>
        public readonly int usedChannels;
        /// <summary>True when sampled with mesh UV directly and free of special use. / 直接网格 UV 采样且无特殊用途。</summary>
        public readonly bool meshUv;

        public ATOPropRule(string name, ATORole role, int usedChannels, bool meshUv)
        {
            this.name = name;
            this.role = role;
            this.usedChannels = usedChannels;
            this.meshUv = meshUv;
        }
    }

    /// <summary>
    /// Texture property table for lilToon-like shaders.
    /// meshUv=false entries are recognized-but-special (matcap, ramp, fur,
    /// audiolink, parallax, dissolve, glitter, dither, gradient, tri-mask...):
    /// they are never optimized; anything NOT in this table is unknown and
    /// forces conservative whitelist of that texture only.
    /// lilToon 系着色器的贴图属性表。meshUv=false 为"已识别但特殊"的贴图
    /// （matcap/阴影 ramp/毛发/AudioLink/视差/溶解/闪粉/抖动/渐变/三角蒙版等），
    /// 永不优化；不在表中的属性按未知处理，仅使对应贴图白名单化。
    /// </summary>
    public static class ATOLilToonTable
    {
        // Channel masks / 通道位
        private const int R = 1, RGB = 7, RGBA = 15;

        public static readonly ATOPropRule[] Rules =
        {
            // Main color chain / 主色链
            new ATOPropRule("_MainTex", ATORole.Main, RGBA, true),
            new ATOPropRule("_Main2ndTex", ATORole.MainLayer, RGBA, true),
            new ATOPropRule("_Main3rdTex", ATORole.MainLayer, RGBA, true),
            new ATOPropRule("_BaseMap", ATORole.Main, RGBA, true),
            new ATOPropRule("_BaseColorMap", ATORole.Main, RGBA, true),

            // Normal maps / 法线
            new ATOPropRule("_BumpMap", ATORole.Normal, RGB, true),
            new ATOPropRule("_Bump2ndMap", ATORole.Normal, RGB, true),
            new ATOPropRule("_Bump2ndScaleMask", ATORole.Mask, R, true),

            // Masks / 蒙版
            new ATOPropRule("_Main2ndBlendMask", ATORole.Mask, R, true),
            new ATOPropRule("_Main3rdBlendMask", ATORole.Mask, R, true),
            new ATOPropRule("_MainColorAdjustMask", ATORole.Mask, R, true),
            new ATOPropRule("_MainGradationTex", ATORole.Mask, RGB, true),
            new ATOPropRule("_AlphaMask", ATORole.Mask, R, true),
            new ATOPropRule("_ShadowStrengthMask", ATORole.Mask, R, true),
            new ATOPropRule("_ShadowBorderMask", ATORole.Mask, R, true),
            new ATOPropRule("_ShadowBlurMask", ATORole.Mask, R, true),
            new ATOPropRule("_EmissionBlendMask", ATORole.Mask, R, true),
            new ATOPropRule("_Emission2ndBlendMask", ATORole.Mask, R, true),
            new ATOPropRule("_MetallicGlossMap", ATORole.Mask, R, true),
            new ATOPropRule("_SmoothnessTex", ATORole.Mask, R, true),
            new ATOPropRule("_OutlineWidthMask", ATORole.Mask, R, true),

            // Color-side textures (sRGB, no alpha semantics) / 色彩类贴图
            new ATOPropRule("_ShadowColorTex", ATORole.Emission, RGB, true),
            new ATOPropRule("_Shadow2ndColorTex", ATORole.Emission, RGB, true),
            new ATOPropRule("_Shadow3rdColorTex", ATORole.Emission, RGB, true),
            new ATOPropRule("_EmissionMap", ATORole.Emission, RGB, true),
            new ATOPropRule("_Emission2ndMap", ATORole.Emission, RGB, true),
            new ATOPropRule("_RimColorTex", ATORole.Emission, RGB, true),
            new ATOPropRule("_RimShadeMask", ATORole.Mask, R, true),
            new ATOPropRule("_BacklightColorTex", ATORole.Emission, RGB, true),
            new ATOPropRule("_ReflectionColorTex", ATORole.Emission, RGB, true),
            new ATOPropRule("_OutlineTex", ATORole.Emission, RGB, true),

            // Recognized special-purpose (never optimized) / 已识别的特殊用途（不优化）
            new ATOPropRule("_MainGradationStrength", ATORole.Unknown, RGBA, false), // guard: float prop shadows / 防御
            new ATOPropRule("_EmissionGradTex", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_Emission2ndGradTex", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_MatCapTex", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_MatCap2ndTex", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_MatCapBumpMap", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_MatCap2ndBumpMap", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_MatCapBlendMask", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_MatCap2ndBlendMask", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_Ramp", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_OutlineVectorTex", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_FurLengthMask", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_FurMask", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_FurNoiseMask", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_FurVectorTex", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_AudioLinkMask", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_AudioLinkLocalMap", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_AudioLinkMask_ScrollRotate", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_AudioLinkMask_UVMode", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_AnisotropyTangentMap", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_AnisotropyScaleMask", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_AnisotropyShiftNoiseMask", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_ParallaxMap", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_DissolveMask", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_DissolveNoiseMask", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_Main2ndDissolveMask", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_Main3rdDissolveMask", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_Main2ndDissolveNoiseMask", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_Main3rdDissolveNoiseMask", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_GlitterColorTex", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_GlitterColorTex_UVMode", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_GlitterShapeTex", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_TriMask", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_DitherTex", ATORole.Unknown, RGBA, false),
            new ATOPropRule("_ReflectionCubeTex", ATORole.Unknown, RGBA, false),
        };

        private static Dictionary<string, ATOPropRule> _byName;

        /// <summary>Lookup rule by property name. / 按属性名查规则。</summary>
        public static bool TryGet(string prop, out ATOPropRule rule)
        {
            if (_byName == null)
            {
                _byName = new Dictionary<string, ATOPropRule>();
                foreach (var r in Rules) _byName[r.name] = r;
            }
            return _byName.TryGetValue(prop, out rule);
        }

        // ----- UV-affecting float properties which must stay at defaults -----
        // ----- 影响 UV 的浮点属性：必须保持默认值 -----

        /// <summary>
        /// Properties that must equal 0 (or listed default) for a texture to
        /// remain optimizable. If any of them is non-default, textures of the
        /// associated block act as whitelist.
        /// 必须为默认值才能保证相关贴图可优化的属性；任一非默认则相关贴图白名单化。
        /// </summary>
        public static readonly (string prop, float safeValue, string affectsSlot)[] ZeroChecks =
        {
            // Scroll/rotate vectors: x/y = scroll z=angle ... all must be zero / 平移旋转全部须为0
            ("_MainTex_ScrollRotate", -999f, "_MainTex"), // sentinel: vector must be exactly zero / 哨兵：向量必须为全零
            ("_Main2ndTex_ScrollRotate", -999f, "_Main2ndTex"),
            ("_Main3rdTex_ScrollRotate", -999f, "_Main3rdTex"),
            // UV mode must be 0 (UV0) / UV 模式必须为 0（UV0）
            ("_Main2ndTex_UVMode", 0f, "_Main2ndTex"),
            ("_Main3rdTex_UVMode", 0f, "_Main3rdTex"),
            // Decal / MSDF must be off / Decal 与 MSDF 必须关闭
            ("_Main2ndTexIsDecal", 0f, "_Main2ndTex"),
            ("_Main3rdTexIsDecal", 0f, "_Main3rdTex"),
            ("_Main2ndTexIsMSDF", 0f, "_Main2ndTex"),
            ("_Main3rdTexIsMSDF", 0f, "_Main3rdTex"),
            // Backface UV shift must be 0 / 背面 UV 平移必须为 0
            ("_ShiftBackfaceUV", 0f, "_MainTex"),
            // Parallax (POM) off: parallax view-offsets uvMain / 视差（POM）必须关闭
            ("_UseParallax", 0f, "_MainTex"),
            ("_UsePOM", 0f, "_MainTex"),
        };
    }

    /// <summary>
    /// Rules for standard-like shaders (Unity Standard / URP Lit / Poiyomi-ish
    /// keyword-compatible). These only apply when the property actually exists
    /// on the material. ST identity is verified per slot afterwards.
    /// 标准关键字着色器规则（Standard / URP Lit / Poiyomi 风格关键字兼容）。
    /// 仅在材质确实存在该属性时生效；之后再逐槽验证 ST 恒等。
    /// </summary>
    public static class ATOStandardTable
    {
        public static readonly ATOPropRule[] Rules =
        {
            new ATOPropRule("_MainTex", ATORole.Main, 15, true),
            new ATOPropRule("_BaseMap", ATORole.Main, 15, true),
            new ATOPropRule("_BaseColorMap", ATORole.Main, 15, true),
            new ATOPropRule("_BumpMap", ATORole.Normal, 7, true),
            new ATOPropRule("_NormalMap", ATORole.Normal, 7, true),
            new ATOPropRule("_EmissionMap", ATORole.Emission, 7, true),
            new ATOPropRule("_MetallicGlossMap", ATORole.Mask, 1, true),
            new ATOPropRule("_SpecGlossMap", ATORole.Mask, 1, true),
            new ATOPropRule("_OcclusionMap", ATORole.Mask, 1, true),
            // Recognized but special: detail/parallax maps use tiling/offsets / 已识别但特殊：细节/视差贴图使用平铺
            new ATOPropRule("_DetailAlbedoMap", ATORole.Unknown, 15, false),
            new ATOPropRule("_DetailNormalMap", ATORole.Unknown, 15, false),
            new ATOPropRule("_ParallaxMap", ATORole.Unknown, 15, false),
        };
    }
}
