using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Analyzes shader/material property tables to decide, per texture slot: category, mesh UV
    /// channel, and safety (ST/scroll/rotation/decal/MSDF/non-mesh usage).
    ///
    /// lilToon is handled by a property-name table derived from lilToon 2.3.4 shader sources and
    /// cross-checked against AAO's ShaderInformation.Liltoon implementation; future lilToon
    /// versions keep working because the table is keyed on stable property names and feature
    /// floats (_UseBumpMap, ...). Unknown lilToon/generic texture slots are whitelisted with a
    /// warning instead of guessed.
    ///
    /// / 分析材质属性表，为每个贴图槽位判定类别、网格UV通道与安全性（ST/滚动/旋转/贴花/非网格UV）。
    /// lilToon 表基于 2.3.4 着色器源码并与 AAO 的 ShaderInformation.Liltoon 实现交叉验证；
    /// 属性名与特性 float 稳定，因此尽可能兼容未来版本。未知槽位一律白名单+警告，绝不猜测。
    /// </summary>
    internal static class ShaderAnalyzer
    {
        // ------------------------------------------------------------------ public API
        internal static MaterialAnalysis Analyze(Material mat)
        {
            if (_cache.TryGetValue(mat, out var cached)) return cached;
            var analysis = AnalyzeInternal(mat);
            _cache[mat] = analysis;
            return analysis;
        }

        internal static void ClearCache() => _cache.Clear();

        private static readonly Dictionary<Material, MaterialAnalysis> _cache =
            new Dictionary<Material, MaterialAnalysis>();

        // ------------------------------------------------------------------ internals
        private static MaterialAnalysis AnalyzeInternal(Material mat)
        {
            var a = new MaterialAnalysis { material = mat };
            if (mat == null || mat.shader == null)
            {
                a.unknown = true;
                a.unknownReason = "null material/shader";
                return a;
            }

            try
            {
                // external classifiers run first (third-party extension point)
                // 外部分类器优先（第三方扩展点）
                if (!TryExternal(mat, a))
                {
                    a.isLilToon = IsLilToon(mat.shader);
                    if (a.isLilToon) AnalyzeLilToon(mat, a);
                    else AnalyzeGeneric(mat, a);
                }
                AnalyzeAlpha(mat, a);
                if (a.alphaCandidates.Count == 0)
                    a.alphaCandidates.Add((a.alphaMode, a.cutoff));
            }
            catch (Exception e)
            {
                a.unknown = true;
                a.unknownReason = $"analyzer exception: {e.Message}";
            }

            return a;
        }

        /// <summary>Run registered third-party classifiers; true when one owned the material. / 运行第三方分类器。</summary>
        private static bool TryExternal(Material mat, MaterialAnalysis a)
        {
            bool any = false;
            foreach (var prop in mat.GetTexturePropertyNames())
            {
                if (!(mat.GetTexture(prop) is Texture2D tex)) continue;
                if (!ATOApi.TryClassifyExternal(mat, prop, tex, out var cat, out var uv, out var safe, out var reason))
                    continue;
                any = true;
                a.slots.Add(new TexSlot
                {
                    property = prop, texture = tex, category = cat, uvChannel = uv,
                    safe = safe && uv >= 0, unsafeReason = reason,
                });
            }
            return any;
        }

        internal static bool IsLilToon(Shader shader)
        {
            var n = shader.name;
            return n.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0
                   || n.StartsWith("Hidden/lts", StringComparison.OrdinalIgnoreCase)
                   || n.StartsWith("_lil/", StringComparison.OrdinalIgnoreCase);
        }

        // ================================================================== lilToon
        //
        // Semantics mirrored from lilToon 2.3.4 shader includes (lil_common_frag.hlsl) and AAO's
        // LiltoonShaderInformation. / 语义取自 lilToon 2.3.4 源码与 AAO 的实现。
        private static void AnalyzeLilToon(Material mat, MaterialAnalysis a)
        {
            var shader = mat.shader;

            // uvMain = uv0 transformed by _MainTex_ST + _MainTex_ScrollRotate (+Angle) — must be identity.
            // uvMain 的变换必须为单位矩阵，否则 MainTex 系全部不安全。
            bool uvMainSafe = IsIdentitySt(mat, "_MainTex_ST")
                              && IsZeroVec(mat, "_MainTex_ScrollRotate")
                              && GetFloat(mat, "_ShiftBackfaceUV") == 0f;
            const int uvMain = 0;

            void Add(string prop, TexCategory cat, int uv, bool safe, string reason = null)
            {
                if (!mat.HasProperty(prop)) return;
                var tex = mat.GetTexture(prop) as Texture2D;
                if (tex == null) return; // unassigned slots are never sampled / 未赋值不采样
                // Slots on uvMain additionally require uvMain to be transform-safe.
                // 跟随 uvMain 的槽位还要求 uvMain 本身无变换。
                bool followsUvMain = uv == uvMain;
                bool finalSafe = safe && string.IsNullOrEmpty(reason) && (!followsUvMain || uvMainSafe);
                a.slots.Add(new TexSlot
                {
                    property = prop, texture = tex, category = cat, uvChannel = uv,
                    safe = finalSafe, unsafeReason = reason,
                });
            }

            bool Flag(string prop) => GetFloat(mat, prop) != 0f;

            // ---- main color family / 主色系 ----
            Add("_MainTex", TexCategory.Color, uvMain, uvMainSafe);
            Add("_BaseMap", TexCategory.Color, uvMain, uvMainSafe);       // dummy alias / 别名
            Add("_BaseColorMap", TexCategory.Color, uvMain, uvMainSafe);  // dummy alias / 别名
            Add("_MainColorAdjustMask", TexCategory.Mask, uvMain, uvMainSafe);

            // alpha mask has its own ST / alpha 蒙版带自己的 ST
            bool alphaMaskSafe = IsIdentitySt(mat, "_AlphaMask_ST") && uvMainSafe;
            Add("_AlphaMask", TexCategory.Mask, uvMain, alphaMaskSafe);

            // ---- 2nd/3rd layer textures: decal matrix checks / 第2/3层贴图：贴花矩阵检查 ----
            foreach (var baseName in new[] { "_Main2nd", "_Main3rd" })
            {
                var texProp = baseName + "Tex";
                if (!Flag($"_Use{baseName}Tex")) continue;
                int uv = UvModeChannel(mat, texProp + "_UVMode", out var uvModeBad);
                bool decalSafe = DecalFamilySafe(mat, texProp);
                Add(texProp, TexCategory.Color, uv, decalSafe && !uvModeBad,
                    uvModeBad ? texProp + "_UVMode>=4 (non-mesh UV)" : null);
                Add(baseName + "BlendMask", TexCategory.Mask, uvMain, uvMainSafe);
                // dissolve masks use complex UV; keep them out / 溶解蒙版UV复杂，白名单
                Add(baseName + "DissolveMask", TexCategory.Mask, -1, false, "dissolve");
                Add(baseName + "DissolveNoiseMask", TexCategory.Grayscale, -1, false, "dissolve");
            }

            // ---- normals / 法线 ----
            if (Flag("_UseBumpMap"))
            {
                bool bumpSafe = IsIdentitySt(mat, "_BumpMap_ST") && uvMainSafe;
                Add("_BumpMap", TexCategory.Normal, uvMain, bumpSafe);
            }

            if (Flag("_UseBump2ndMap"))
            {
                int uv = UvModeChannel(mat, "_Bump2ndMap_UVMode", out var bad);
                bool stSafe = IsIdentitySt(mat, "_Bump2ndMap_ST") && !bad;
                Add("_Bump2ndMap", TexCategory.Normal, uv, stSafe, bad ? "_Bump2ndMap_UVMode unknown" : null);
                Add("_Bump2ndScaleMask", TexCategory.Mask, uvMain, uvMainSafe);
            }

            // ---- anisotropy / 各向异性 ----
            if (Flag("_UseAnisotropy"))
            {
                Add("_AnisotropyTangentMap", TexCategory.Normal, uvMain, uvMainSafe);
                Add("_AnisotropyScaleMask", TexCategory.Mask, uvMain, uvMainSafe);
                Add("_AnisotropyShiftNoiseMask", TexCategory.Grayscale, uvMain, uvMainSafe);
            }

            // ---- backlight / 背光 ----
            if (Flag("_UseBacklight"))
                Add("_BacklightColorTex", TexCategory.Color, uvMain, uvMainSafe);

            // ---- shadows / 阴影 ----
            if (Flag("_UseShadow"))
            {
                Add("_ShadowStrengthMask", TexCategory.Mask, uvMain, uvMainSafe);
                Add("_ShadowBorderMask", TexCategory.Mask, uvMain, uvMainSafe);
                Add("_ShadowBlurMask", TexCategory.Mask, uvMain, uvMainSafe);
                // _ShadowColorType==1 → LUT/color-based UV (non-mesh). / ==1 时为颜色查表UV。
                bool shadowColorMesh = GetFloat(mat, "_ShadowColorType") == 0f;
                Add("_ShadowColorTex", TexCategory.Color, shadowColorMesh ? uvMain : -1,
                    shadowColorMesh && uvMainSafe, shadowColorMesh ? null : "_ShadowColorType=1");
                Add("_Shadow2ndColorTex", TexCategory.Color, shadowColorMesh ? uvMain : -1,
                    shadowColorMesh && uvMainSafe, shadowColorMesh ? null : "_ShadowColorType=1");
                Add("_Shadow3rdColorTex", TexCategory.Color, shadowColorMesh ? uvMain : -1,
                    shadowColorMesh && uvMainSafe, shadowColorMesh ? null : "_ShadowColorType=1");
            }

            // ---- rim shade ----
            if (Flag("_UseRimShade"))
                Add("_RimShadeMask", TexCategory.Mask, uvMain, uvMainSafe);

            // ---- reflection / 反射 ----
            if (Flag("_UseReflection"))
            {
                Add("_SmoothnessTex", TexCategory.Mask, uvMain, uvMainSafe);
                Add("_MetallicGlossMap", TexCategory.Mask, uvMain, uvMainSafe);
                Add("_ReflectionColorTex", TexCategory.Color, uvMain, uvMainSafe);
            }

            // ---- matcap / 材质捕获 ----
            if (Flag("_UseMatCap"))
            {
                Add("_MatCapTex", TexCategory.Color, -1, false, "matcap");           // non-mesh UV
                Add("_MatCapBlendMask", TexCategory.Mask, uvMain, uvMainSafe);
                if (Flag("_MatCapCustomNormal"))
                    Add("_MatCapBumpMap", TexCategory.Normal, uvMain, uvMainSafe && IsIdentitySt(mat, "_MatCapBumpMap_ST"));
            }

            if (Flag("_UseMatCap2nd"))
            {
                Add("_MatCap2ndTex", TexCategory.Color, -1, false, "matcap2nd");
                Add("_MatCap2ndBlendMask", TexCategory.Mask, uvMain, uvMainSafe);
                if (Flag("_MatCap2ndCustomNormal"))
                    Add("_MatCap2ndBumpMap", TexCategory.Normal, uvMain, uvMainSafe && IsIdentitySt(mat, "_MatCap2ndBumpMap_ST"));
            }

            // ---- rim light ----
            if (Flag("_UseRim"))
                Add("_RimColorTex", TexCategory.Color, uvMain, uvMainSafe && IsIdentitySt(mat, "_RimColorTex_ST"));

            // ---- glitter / 闪粉 ----
            if (Flag("_UseGlitter"))
            {
                Add("_GlitterColorTex", TexCategory.Color, uvMain, uvMainSafe && IsIdentitySt(mat, "_GlitterColorTex_ST"));
                Add("_GlitterShapeTex", TexCategory.Color, -1, false, "glitter shape");
            }

            // ---- emission / 自发光 ----
            foreach (var e in new[] { ("_Emission", "_UseEmission"), ("_Emission2nd", "_UseEmission2nd") })
            {
                var baseName = e.Item1;
                if (!Flag(e.Item2)) continue;
                int uv = UvModeChannel(mat, baseName + "Map_UVMode", out var bad);
                bool stSafe = IsIdentitySt(mat, baseName + "Map_ST") && IsZeroVec(mat, baseName + "Map_ScrollRotate");
                bool parallax = GetFloat(mat, baseName + "ParallaxDepth") != 0f;
                Add(baseName + "Map", TexCategory.Color, uv,
                    stSafe && !bad && !parallax,
                    bad ? baseName + "Map_UVMode>=4" : parallax ? "emission parallax" : null);
                // blend mask: ScrollRotate!=0 switches it to UV0 with animated scroll → unsafe anyway
                // 蒙版: ScrollRotate 非零时切 UV0 且滚动 → 不安全
                bool maskSafe = IsIdentitySt(mat, baseName + "BlendMask_ST") &&
                                IsZeroVec(mat, baseName + "BlendMask_ScrollRotate") && uvMainSafe;
                Add(baseName + "BlendMask", TexCategory.Mask, uvMain, maskSafe);
                Add(baseName + "GradTex", TexCategory.Color, -1, false, "grad LUT");
            }

            // ---- parallax: POM distorts UV → whitelist / 视差：UV 扭曲，白名单 ----
            if (Flag("_UseParallax"))
                Add("_ParallaxMap", TexCategory.Mask, -1, false, "parallax/POM");

            // ---- audio link: dynamic UV / 音频联动：动态UV，白名单 ----
            Add("_AudioLinkMask", TexCategory.Mask, -1, false, "audiolink");
            Add("_AudioLinkLocalMap", TexCategory.Grayscale, -1, false, "audiolink");

            // ---- outline / 描边 ----
            Add("_OutlineTex", TexCategory.Color, uvMain, uvMainSafe);
            Add("_OutlineWidthMask", TexCategory.Mask, uvMain, uvMainSafe);
            int outlineUv = UvModeChannel(mat, "_OutlineVectorTex_UVMode", out var outlineBad);
            Add("_OutlineVectorTex", TexCategory.Normal, outlineBad ? -1 : outlineUv, !outlineBad,
                outlineBad ? "_OutlineVectorTex_UVMode>=4" : null);

            // ---- fur / 毛发 ----
            Add("_FurNoiseMask", TexCategory.Grayscale, 0, IsIdentitySt(mat, "_FurNoiseMask_ST"));
            Add("_FurMask", TexCategory.Mask, uvMain, uvMainSafe && IsIdentitySt(mat, "_FurMask_ST"));
            Add("_FurLengthMask", TexCategory.Mask, uvMain, uvMainSafe);
            Add("_FurVectorTex", TexCategory.Normal, uvMain, uvMainSafe);

            // ---- known non-mesh misc / 已知非网格UV杂项 ----
            Add("_DitherTex", TexCategory.Grayscale, -1, false, "screen-space dither");
            Add("_MainGradationTex", TexCategory.Color, -1, false, "gradation LUT");

            // Unknown lilToon texture properties (future versions): whitelist + warning.
            // 未知 lilToon 贴图属性（未来版本）：白名单 + 警告。
            MarkUnknownTextureProperties(mat, a);
        }

        /// <summary>Find assigned Texture2D properties not covered by our table. / 找出表中未覆盖且已赋值的贴图属性。</summary>
        private static void MarkUnknownTextureProperties(Material mat, MaterialAnalysis a)
        {
            var known = new HashSet<string>(a.slots.Select(s => s.property));
            int count = mat.shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (mat.shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                var prop = mat.shader.GetPropertyName(i);
                if (known.Contains(prop)) continue;
                var tex = mat.GetTexture(prop) as Texture2D;
                if (tex == null) continue;

                // Try standard naming conventions before whitelisting. / 先按命名约定兜底，再白名单。
                var (cat, safe, reason) = ClassifyByName(prop, mat);
                int uv = safe ? GenericUvChannel(mat, prop) : 0;
                a.slots.Add(new TexSlot
                {
                    property = prop, texture = tex, category = cat,
                    uvChannel = uv,
                    safe = safe && uv >= 0, unsafeReason = reason,
                });
            }
        }

        // ================================================================== generic shaders
        private static void AnalyzeGeneric(Material mat, MaterialAnalysis a)
        {
            var shader = mat.shader;
            int count = shader.GetPropertyCount();

            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                var prop = shader.GetPropertyName(i);
                if (shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) continue;
                var tex = mat.GetTexture(prop) as Texture2D;
                if (tex == null) continue;

                var attrs = GetAttributes(shader, i);
                bool noScaleOffset = shader.GetPropertyFlags(i).HasFlag(ShaderPropertyFlags.NonModifiableTextureData)
                                     || attrs.Contains("noscaleoffset");
                bool isNormal = attrs.Contains("normal") ||
                                prop.IndexOf("bump", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                prop.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isMainTex = attrs.Contains("maintexture") || prop == "_MainTex" || prop == "_BaseMap" ||
                                 prop.IndexOf("albedo", StringComparison.OrdinalIgnoreCase) >= 0;

                // Keyword-gated textures in standard shaders: only "live" when enabled.
                // 标准着色器中关键字控制的贴图：启用才算存活。
                if (IsKeywordGatedAndOff(mat, prop)) continue;

                var (cat, safeByName, reason) = ClassifyByName(prop, mat);
                if (isNormal) cat = TexCategory.Normal;
                else if (cat == TexCategory.Color && !isMainTex && !IsSRgbHint(prop)) cat = TexCategory.Mask;

                bool stSafe = noScaleOffset || (mat.GetTextureScale(prop) == Vector2.one &&
                                                mat.GetTextureOffset(prop) == Vector2.zero);

                int uv = GenericUvChannel(mat, prop);

                a.slots.Add(new TexSlot
                {
                    property = prop, texture = tex, category = cat, uvChannel = uv,
                    safe = safeByName && stSafe && uv >= 0, unsafeReason = reason ?? (stSafe ? null : "non-identity ST"),
                });
            }
        }

        private static readonly string[] GatedPrefixes = { "_DETAIL", "_EMISSION", "_NORMALMAP", "_METALLICGLOSSMAP", "_PARALLAXMAP" };

        private static bool IsKeywordGatedAndOff(Material mat, string prop)
        {
            // Only gate off when the shader actually declares the keyword and it is disabled;
            // when in doubt the texture is treated as used (conservative).
            // 仅当着色器确实声明了关键字且未启用时才视为未使用；拿不准一律按已使用（保守）。
            try
            {
                foreach (var p in GatedPrefixes)
                {
                    if (!prop.StartsWith(p, StringComparison.OrdinalIgnoreCase)) continue;
                    var kw = p; // e.g. "_EMISSIONMAP" → keyword "_EMISSION"
                    if (mat.shader.keywordSpace.Contains(new LocalKeyword(mat.shader, kw)) &&
                        !mat.IsKeywordEnabled(kw))
                        return true;
                }
            }
            catch
            {
                // keywordSpace unavailable → conservative / 不可用时保守处理
            }
            return false;
        }

        private static bool IsSRgbHint(string prop)
        {
            return prop.IndexOf("emission", StringComparison.OrdinalIgnoreCase) >= 0
                   || prop.IndexOf("color", StringComparison.OrdinalIgnoreCase) >= 0
                   || prop.IndexOf("tex", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>Naming-convention classification used as fallback for unknown properties. / 命名约定兜底分类。</summary>
        private static (TexCategory, bool, string) ClassifyByName(string prop, Material mat)
        {
            string p = prop.ToLowerInvariant();
            if (p.Contains("matcap") || p.Contains("decal") || p.Contains("flow") || p.Contains("panorama") ||
                p.Contains("audiolink") || p.Contains("screen") || p.Contains("gradtex") || p.Contains("gradation"))
                return (TexCategory.Color, false, "special/decal-like usage by name");
            if (p.Contains("bump") || p.Contains("normal"))
                return (TexCategory.Normal, true, null);
            if (p.Contains("dissolve"))
                return (TexCategory.Mask, false, "dissolve");
            if (p.Contains("mask") || p.Contains("metallic") || p.Contains("smooth") || p.Contains("occlusion") ||
                p.Contains("roughness") || p.EndsWith("ao") || p.Contains("ao tex"))
                return (TexCategory.Mask, true, null);
            return (TexCategory.Color, true, null);
        }

        private static int GenericUvChannel(Material mat, string prop)
        {
            foreach (var candidate in new[] { prop + "_UVMode", prop + "_UV", prop + "UV", prop + "_UVChannel" })
            {
                if (!mat.HasProperty(candidate)) continue;
                var v = GetFloat(mat, candidate);
                if (v >= 0 && v <= 3) return (int)v;
                return -1; // 4+ → non-mesh / 非网格
            }
            return 0;
        }

        // ================================================================== alpha facts
        private static void AnalyzeAlpha(Material mat, MaterialAnalysis a)
        {
            var n = mat.shader.name.ToLowerInvariant();
            if (a.isLilToon)
            {
                // lilToon variants encode transparency in the shader name. / lilToon 变体名内含透明模式。
                if (n.Contains("cutout")) a.alphaMode = AlphaMode.Cutout;
                else if (n.Contains("trans") || n.Contains("overlay")) a.alphaMode = AlphaMode.Blend;
                else a.alphaMode = AlphaMode.Opaque;
            }
            else
            {
                if (mat.IsKeywordEnabled("_ALPHABLEND_ON") || mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
                    a.alphaMode = AlphaMode.Blend;
                else if (mat.IsKeywordEnabled("_ALPHATEST_ON")) a.alphaMode = AlphaMode.Cutout;
                else if (mat.renderQueue >= 3000) a.alphaMode = AlphaMode.Blend;
                else if (mat.renderQueue >= 2450 && mat.renderQueue < 3000) a.alphaMode = AlphaMode.Cutout;
                else a.alphaMode = AlphaMode.Opaque;
            }

            if (mat.HasProperty("_Cutoff")) a.cutoff = mat.GetFloat("_Cutoff");
        }

        // ================================================================== small helpers
        private static float GetFloat(Material mat, string prop)
        {
            return mat.HasProperty(prop) ? mat.GetFloat(prop) : 0f;
        }

        private static bool IsIdentitySt(Material mat, string prop)
        {
            if (!mat.HasProperty(prop)) return true;
            var v = mat.GetVector(prop);
            return v == new Vector4(1f, 1f, 0f, 0f);
        }

        private static bool IsZeroVec(Material mat, string prop)
        {
            if (!mat.HasProperty(prop)) return true;
            return mat.GetVector(prop) == Vector4.zero;
        }

        /// <summary>
        /// lilToon decal family safety: ST/ScrollRotate/Angle + flip/copy/decal flags + decal
        /// animation, mirroring AAO's LIL_GET_SUBTEX matrix logic. / lilToon 贴花族安全性检查。
        /// </summary>
        private static bool DecalFamilySafe(Material mat, string texProp)
        {
            if (!IsIdentitySt(mat, texProp + "_ST")) return false;
            if (!IsZeroVec(mat, texProp + "_ScrollRotate")) return false;
            if (GetFloat(mat, texProp + "Angle") != 0f) return false;
            if (GetFloat(mat, texProp + "ShouldCopy") != 0f) return false;
            if (GetFloat(mat, texProp + "ShouldFlipCopy") != 0f) return false;
            if (GetFloat(mat, texProp + "ShouldFlipMirror") != 0f) return false;
            if (GetFloat(mat, texProp + "IsLeftOnly") != 0f) return false;
            if (GetFloat(mat, texProp + "IsRightOnly") != 0f) return false;
            var decalAnim = mat.GetVector(texProp + "DecalAnimation");
            if (decalAnim != new Vector4(1f, 1f, 1f, 30f)) return false;
            return true;
        }

        /// <summary>Resolve a `_XXX_UVMode` property: 0..3 mesh channel; ≥4 or missing→flag. / 解析 UVMode 属性。</summary>
        private static int UvModeChannel(Material mat, string prop, out bool nonMeshOrUnknown)
        {
            nonMeshOrUnknown = false;
            if (!mat.HasProperty(prop))
            {
                nonMeshOrUnknown = true; // unknown → treat as non-mesh (whitelist) / 未知按非网格处理
                return 0;
            }
            var v = GetFloat(mat, prop);
            if (v >= 0 && v <= 3) return (int)v;
            if (v >= 4) { nonMeshOrUnknown = true; return -1; }
            nonMeshOrUnknown = true; // negative/NaN → unknown / 负数视为未知
            return 0;
        }

        private static HashSet<string> GetAttributes(Shader shader, int index)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var raw in ShaderUtil.GetShaderPropertyAttributes(shader, index) ?? Array.Empty<string>())
                {
                    var s = raw.Trim('[', ']', ' ');
                    var sp = s.IndexOf(' ');
                    if (sp > 0) s = s.Substring(0, sp);
                    set.Add(s.ToLowerInvariant());
                }
            }
            catch
            {
                // attribute inspection is best-effort / 属性检查尽力而为
            }
            return set;
        }
    }
}
