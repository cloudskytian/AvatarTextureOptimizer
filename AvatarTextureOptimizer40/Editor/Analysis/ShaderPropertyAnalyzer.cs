using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.Ato.Editor.Analysis
{
    /// <summary>
    /// Classifies a shader's texture properties (main color / normal / mask / emission / data) and
    /// detects whether a property samples UV with any ST/tiling/offset/rotation/decals.
    ///
    /// Design is data-driven: we parse the shader's property block attributes ([Normal],
    /// [MainTexture]/[MainColor], [NoScaleOffset]) AND keep a curated table for lilToon and the
    /// Standard/URP/Lit shaders. If we cannot confidently classify a shader/property, we return it
    /// as Unknown so the pipeline treats it as whitelist + warning (safety first).
    /// 数据驱动的着色器贴图属性分类器：解析属性块特性（[Normal]/[MainTexture]/[MainColor]/
    /// [NoScaleOffset]），并内置 lilToon 与 Standard/URP/Lit 表。无法可靠识别时返回 Unknown，
    /// 流程将其按白名单处理并报 warning（安全优先）。
    /// </summary>
    internal static class ShaderPropertyAnalyzer
    {
        internal enum TexRole { Color, Normal, Mask, Emission, Data, Unknown }

        internal readonly struct PropertyInfo
        {
            public readonly string Name;
            public readonly TexRole Role;
            public readonly bool NoScaleOffset; // [NoScaleOffset] => no ST / 该属性无 ST
            public readonly TextureKind Kind;
            public readonly bool GrayscaleHint;
            public PropertyInfo(string name, TexRole role, bool noScaleOffset, bool grayscale = false)
            {
                Name = name; Role = role; NoScaleOffset = noScaleOffset; GrayscaleHint = grayscale;
                Kind = role switch
                {
                    TexRole.Color => TextureKind.Color,
                    TexRole.Normal => TextureKind.Normal,
                    TexRole.Mask => TextureKind.Mask,
                    TexRole.Emission => TextureKind.Emission,
                    _ => TextureKind.Data,
                };
            }
        }

        // Curated table. Keys are property names (Unity normalizes to uppercase leading _).
        // 内置表，键为属性名。
        private static readonly Dictionary<string, PropertyInfo> Known = new(StringComparer.Ordinal)
        {
            // ---- Unity Standard / URP Lit / HDRP Lit ----
            { "_MainTex",         new PropertyInfo("_MainTex", TexRole.Color, false) },
            { "_BaseMap",         new PropertyInfo("_BaseMap", TexRole.Color, false) }, // URP
            { "_BaseColorMap",    new PropertyInfo("_BaseColorMap", TexRole.Color, false) }, // HDRP
            { "_BumpMap",         new PropertyInfo("_BumpMap", TexRole.Normal, true) },
            { "_BumpScale",       default }, // scalar, ignore
            { "_MetallicGlossMap",new PropertyInfo("_MetallicGlossMap", TexRole.Mask, true, true) },
            { "_MaskMap",         new PropertyInfo("_MaskMap", TexRole.Mask, true, true) }, // HDRP
            { "_OcclusionMap",    new PropertyInfo("_OcclusionMap", TexRole.Mask, true, true) },
            { "_ParallaxMap",     new PropertyInfo("_ParallaxMap", TexRole.Data, true) },
            { "_EmissionMap",     new PropertyInfo("_EmissionMap", TexRole.Emission, false) },
            { "_EmissiveColorMap",new PropertyInfo("_EmissiveColorMap", TexRole.Emission, false) },
            { "_DetailAlbedoMap", new PropertyInfo("_DetailAlbedoMap", TexRole.Color, false) },
            { "_DetailMask",      new PropertyInfo("_DetailMask", TexRole.Mask, true, true) },
            { "_DetailNormalMap", new PropertyInfo("_DetailNormalMap", TexRole.Normal, true) },

            // ---- lilToon (verified from lil_common_input*.hlsl; only MESH-UV sampled maps are
            //      included. MatCap, reflection, audio link etc. use VIEW/object/other coordinates
            //      and MUST NOT be treated as mesh-UV textures — repacking them would be wrong.
            //      _Main2ndTex/_Main3rdTex support independent rotation/angle/scale so they are
            //      excluded too (conservative; treated as whitelist + warning via the unknown path).
            //      仅包含按网格 UV 采样的贴图。MatCap/反射/AudioLink 使用视图/物体坐标，不能重排；
            //      _Main2ndTex/_Main3rdTex 支持独立旋转/角度/缩放，同样排除（保守按白名单处理）。----
            { "_OutlineTex",      new PropertyInfo("_OutlineTex", TexRole.Color, false) },
            { "_Bump2ndMap",      new PropertyInfo("_Bump2ndMap", TexRole.Normal, true) },
            { "_TriMask",         new PropertyInfo("_TriMask", TexRole.Mask, true, true) },
            { "_Main2ndBlendMask",new PropertyInfo("_Main2ndBlendMask", TexRole.Mask, true, true) },
            { "_Main3rdBlendMask",new PropertyInfo("_Main3rdBlendMask", TexRole.Mask, true, true) },
            { "_EmissionBlendMask", new PropertyInfo("_EmissionBlendMask", TexRole.Mask, true, true) },
            { "_Emission2ndBlendMask",new PropertyInfo("_Emission2ndBlendMask", TexRole.Mask, true, true) },
            { "_Emission2ndMap",  new PropertyInfo("_Emission2ndMap", TexRole.Emission, false) },
            { "_AlphaMask",       new PropertyInfo("_AlphaMask", TexRole.Mask, true, true) },
            { "_ShadowStrengthMask", new PropertyInfo("_ShadowStrengthMask", TexRole.Mask, true, true) },
            { "_ShadowBorderMask", new PropertyInfo("_ShadowBorderMask", TexRole.Mask, true, true) },
            { "_ShadowBlurMask",  new PropertyInfo("_ShadowBlurMask", TexRole.Mask, true, true) },
            { "_SmoothnessTex",   new PropertyInfo("_SmoothnessTex", TexRole.Mask, true, true) },
            { "_OutlineWidthMask",new PropertyInfo("_OutlineWidthMask", TexRole.Mask, true, true) },
            { "_RimShadeMask",    new PropertyInfo("_RimShadeMask", TexRole.Mask, true, true) },
            { "_DissolveMask",    new PropertyInfo("_DissolveMask", TexRole.Mask, true, true) },
            { "_DissolveNoiseMask",new PropertyInfo("_DissolveNoiseMask", TexRole.Mask, true, true) },
            { "_FurMask",         new PropertyInfo("_FurMask", TexRole.Mask, true, true) },
            { "_FurLengthMask",   new PropertyInfo("_FurLengthMask", TexRole.Mask, true, true) },
            { "_FurNoiseMask",    new PropertyInfo("_FurNoiseMask", TexRole.Mask, true, true) },
            { "_AnisotropyScaleMask", new PropertyInfo("_AnisotropyScaleMask", TexRole.Mask, true, true) },
            // Note: shadow/rim color textures in lilToon are gradient LUTs sampled by lighting, not
            // mesh UV, so they are intentionally excluded. / 阴影/边缘色贴图是按光照采样的渐变 LUT，
            // 非网格 UV，故意排除。
        };

        // Properties that imply transform/decal usage if present and not [NoScaleOffset].
        // 这些属性存在且非 [NoScaleOffset] 时，意味着可能有 ST/贴花变换
        private static readonly HashSet<string> TransformSensitive = new(StringComparer.Ordinal)
        {
            "_MainTex", "_BaseMap", "_BaseColorMap", "_DetailAlbedoMap",
            "_Main2ndTex", "_Main3rdTex", "_OutlineTex",
            "_EmissionMap", "_Emission2ndMap", "_EmissiveColorMap",
            "_MatCapTex", "_MatCap2ndTex", "_ReflectionColorTex", "_BacklightColorTex",
        };

        /// <summary>
        /// Returns the texture properties of a shader we understand. Unknown textures that we cannot
        /// classify are omitted (caller treats them as whitelist). Returns false if the shader itself
        /// is unrecognized/unsafe (caller skips all its textures with a warning).
        /// 返回能识别的贴图属性；无法分类的贴图不返回（调用方按白名单处理）。若着色器本身无法识别
        /// 或不安全，返回 false（调用方跳过其全部贴图并报 warning）。
        /// </summary>
        public static bool TryGetProperties(Shader shader, out List<PropertyInfo> result)
        {
            result = new List<PropertyInfo>();
            if (shader == null) return false;

            string name = shader.name ?? "";
            bool isKnownShader =
                name.StartsWith("Standard", StringComparison.Ordinal) ||
                name.Contains("Universal Render Pipeline/Lit", StringComparison.Ordinal) ||
                name.Contains("URP/", StringComparison.Ordinal) ||
                name.Contains("HDRP/", StringComparison.Ordinal) ||
                name.StartsWith("_lil/", StringComparison.Ordinal) ||
                name.StartsWith("lilToon", StringComparison.Ordinal) ||
                name.IndexOf("liltoon", StringComparison.OrdinalIgnoreCase) >= 0;

            int count = shader.GetPropertyCount();
            bool foundAnyKnown = false;
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                string pname = shader.GetPropertyName(i);
                var flags = shader.GetPropertyAttributes(i);

                bool noScaleOffset = Array.IndexOf(flags, "NoScaleOffset") >= 0;
                bool isNormal = Array.IndexOf(flags, "Normal") >= 0;

                if (Known.TryGetValue(pname, out var info) && info.Name != null)
                {
                    // [Normal] attribute can confirm/override role / [Normal] 可确认角色
                    if (isNormal) info = new PropertyInfo(pname, TexRole.Normal, info.NoScaleOffset, info.GrayscaleHint);
                    result.Add(info);
                    foundAnyKnown = true;
                }
                else if (isNormal)
                {
                    result.Add(new PropertyInfo(pname, TexRole.Normal, noScaleOffset));
                    foundAnyKnown = true;
                }
                else if (Array.IndexOf(flags, "MainTexture") >= 0)
                {
                    result.Add(new PropertyInfo(pname, TexRole.Color, noScaleOffset));
                    foundAnyKnown = true;
                }
                // else: unknown texture property — leave out (treat as whitelist)
                //       未知贴图属性：不加入（按白名单处理）
            }

            // If shader is known (lilToon/Standard/URP) we trust our table even if a particular map is
            // absent. If it's wholly unknown and we found nothing, it's unsafe.
            // 对着色器有把握时即使没有贴图也算成功；完全未知且无识别项则视为不安全。
            return isKnownShader || foundAnyKnown;
        }

        /// <summary>
        /// True if this material/property uses UV ST (tiling/offset). A material is considered
        /// transforming if its scale != (1,1) or offset != (0,0) for a transform-sensitive property
        /// that is NOT [NoScaleOffset]. Animation of these is checked separately.
        /// 判定材质/属性是否使用 UV ST（tiling/offset）。transform-sensitive 且非 NoScaleOffset 的
        /// 属性，其 scale!=(1,1) 或 offset!=(0,0) 即视为有变换。动画检查另做。
        /// </summary>
        public static bool HasStTransform(Material mat, PropertyInfo prop)
        {
            if (mat == null) return false;
            if (prop.NoScaleOffset) return false;
            if (!TransformSensitive.Contains(prop.Name)) return false;
            var st = mat.GetTextureScale(prop.Name);
            var off = mat.GetTextureOffset(prop.Name);
            return !Mathf.Approximately(st.x, 1f) || !Mathf.Approximately(st.y, 1f)
                || !Mathf.Approximately(off.x, 0f) || !Mathf.Approximately(off.y, 0f);
        }

        /// <summary>
        /// Determine UV channel used by a texture property. For standard shaders this is UV0 for all
        /// except detail maps (UV2 in Standard). For lilToon we reflect its UV controls.
        /// 取贴图属性使用的 UV 通道。标准着色器除 detail 用 UV2 外均为 UV0；lilToon 反射其 UV 控制。
        /// </summary>
        public static int GetUvChannel(Material mat, PropertyInfo prop)
        {
            if (prop.Name == "_DetailAlbedoMap" || prop.Name == "_DetailNormalMap" || prop.Name == "_DetailMask")
                return mat != null && mat.HasProperty("_UVSec") ? (int)mat.GetFloat("_UVSec") : 1;
            // lilToon: _uvMask and per-map UV selection; we default to channel 0 and upgrade only when
            // the material enables a non-zero channel. Most avatars use UV0.
            // lilToon 默认 UV0，仅在显式启用非零时切换。
            if (mat != null && prop.Name.StartsWith("_Main3rd", StringComparison.Ordinal) && mat.HasProperty("_Main3rdMapUV"))
                return Mathf.Clamp((int)mat.GetFloat("_Main3rdMapUV"), 0, 7);
            return 0;
        }
    }
}
