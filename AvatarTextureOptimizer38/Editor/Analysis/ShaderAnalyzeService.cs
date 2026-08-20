using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Analyzes materials using registered analyzers, then lilToon, then standard keywords, then generic properties.
    /// 依次：扩展分析器 → lilToon → 标准关键字 → 通用属性表。
    /// Never guesses undocumented APIs; lilToon names come from lilMaterialProperties.cs / shader property blocks.
    /// 不猜测未读过的 API；lilToon 名称来自源码属性表。
    /// </summary>
    public static class ShaderAnalyzeService
    {
        private static readonly Regex TexProp = new Regex(@"_ST$|_HDR$|_TexelSize$", RegexOptions.Compiled);

        public static ShaderAnalysisResult Analyze(Material mat)
        {
            if (mat == null || mat.shader == null)
            {
                return new ShaderAnalysisResult { Supported = false, UnsupportedReason = "null material/shader" };
            }

            foreach (var ext in ShaderAnalyzerRegistry.GetAnalyzers())
            {
                try
                {
                    if (ext.TryAnalyze(mat, out var r) && r != null) return r;
                }
                catch (Exception e)
                {
                    AtoLog.Warn($"Extension analyzer {ext.Name} failed: {e.Message}");
                }
            }

            var name = mat.shader.name ?? "";
            if (IsLilToon(name)) return LilToonAnalyzer.Analyze(mat);

            var std = StandardKeywordAnalyzer.Analyze(mat);
            if (std.Slots.Count > 0) return std;

            return GenericPropertyAnalyzer.Analyze(mat);
        }

        public static bool IsLilToon(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            var n = shaderName.ToLowerInvariant();
            return n.Contains("liltoon") || n.Contains("hidden/ltspass") || n.StartsWith("hidden/lts") ||
                   n.Contains("ltsl") || n.Contains("_lil/");
        }

        public static bool IsIdentitySt(Vector4 st)
        {
            return Mathf.Abs(st.x - 1f) < 1e-4f && Mathf.Abs(st.y - 1f) < 1e-4f &&
                   Mathf.Abs(st.z) < 1e-4f && Mathf.Abs(st.w) < 1e-4f;
        }

        public static Vector4 GetSt(Material mat, string texProp)
        {
            var stName = texProp + "_ST";
            if (mat.HasProperty(stName)) return mat.GetVector(stName);
            var scale = mat.GetTextureScale(texProp);
            var offset = mat.GetTextureOffset(texProp);
            return new Vector4(scale.x, scale.y, offset.x, offset.y);
        }

        public static int ReadUvMode(Material mat, string texProp)
        {
            var names = new[] { texProp + "_UVMode", texProp + "UVMode", texProp + "_UV" };
            foreach (var n in names)
            {
                if (mat.HasProperty(n)) return Mathf.RoundToInt(mat.GetFloat(n));
            }
            return 0;
        }
    }

    /// <summary>
    /// lilToon slot table derived from Editor/lilInspector/lilMaterialProperties.cs.
    /// 来自 lilToon 源码的贴图槽表。
    /// </summary>
    public static class LilToonAnalyzer
    {
        private struct SlotDef
        {
            public string Prop;
            public TextureUsageKind Usage;
            public string UseFlag; // _UseBumpMap etc. empty = always
            public bool MainUv;
            public bool UnsafeAlways;
            public string UnsafeIf;
        }

        private static readonly SlotDef[] Slots =
        {
            new SlotDef { Prop = "_MainTex", Usage = TextureUsageKind.Albedo, MainUv = true },
            new SlotDef { Prop = "_MainColorAdjustMask", Usage = TextureUsageKind.Mask, MainUv = true },
            new SlotDef { Prop = "_BumpMap", Usage = TextureUsageKind.Normal, UseFlag = "_UseBumpMap", MainUv = true },
            new SlotDef { Prop = "_AlphaMask", Usage = TextureUsageKind.Mask, MainUv = true },
            new SlotDef { Prop = "_ShadowColorTex", Usage = TextureUsageKind.Albedo, MainUv = true },
            new SlotDef { Prop = "_Shadow2ndColorTex", Usage = TextureUsageKind.Albedo, MainUv = true },
            new SlotDef { Prop = "_Shadow3rdColorTex", Usage = TextureUsageKind.Albedo, MainUv = true },
            new SlotDef { Prop = "_ShadowBorderMask", Usage = TextureUsageKind.Mask, MainUv = true },
            new SlotDef { Prop = "_ShadowBlurMask", Usage = TextureUsageKind.Mask, MainUv = true },
            new SlotDef { Prop = "_ShadowStrengthMask", Usage = TextureUsageKind.Mask, MainUv = true },
            new SlotDef { Prop = "_RimColorTex", Usage = TextureUsageKind.Albedo, MainUv = true },
            new SlotDef { Prop = "_EmissionMap", Usage = TextureUsageKind.Emission, UseFlag = "_UseEmission", MainUv = true },
            new SlotDef { Prop = "_EmissionBlendMask", Usage = TextureUsageKind.Mask, UseFlag = "_UseEmission", MainUv = true },
            new SlotDef { Prop = "_Emission2ndMap", Usage = TextureUsageKind.Emission, UseFlag = "_UseEmission2nd", MainUv = true },
            new SlotDef { Prop = "_Emission2ndBlendMask", Usage = TextureUsageKind.Mask, UseFlag = "_UseEmission2nd", MainUv = true },
            new SlotDef { Prop = "_OutlineTex", Usage = TextureUsageKind.Albedo, MainUv = true },
            new SlotDef { Prop = "_OutlineWidthMask", Usage = TextureUsageKind.Mask, MainUv = true },
            new SlotDef { Prop = "_MainGradationTex", Usage = TextureUsageKind.SpecialDeforming, UnsafeAlways = true },
            new SlotDef { Prop = "_ParallaxMap", Usage = TextureUsageKind.SpecialDeforming, UnsafeAlways = true },
            new SlotDef { Prop = "_MatCapTex", Usage = TextureUsageKind.SpecialDeforming, UnsafeAlways = true },
            new SlotDef { Prop = "_MatCap2ndTex", Usage = TextureUsageKind.SpecialDeforming, UnsafeAlways = true },
            new SlotDef { Prop = "_MatCapBlendMask", Usage = TextureUsageKind.SpecialDeforming, UnsafeAlways = true },
            new SlotDef { Prop = "_MatCapBumpMap", Usage = TextureUsageKind.SpecialDeforming, UnsafeAlways = true },
            new SlotDef { Prop = "_Cubemap", Usage = TextureUsageKind.SpecialDeforming, UnsafeAlways = true },
            new SlotDef { Prop = "_Main2ndTex", Usage = TextureUsageKind.Albedo, UseFlag = "_UseMain2ndTex" },
            new SlotDef { Prop = "_Main3rdTex", Usage = TextureUsageKind.Albedo, UseFlag = "_UseMain3rdTex" },
            new SlotDef { Prop = "_Bump2ndMap", Usage = TextureUsageKind.Normal, UseFlag = "_UseBump2ndMap" },
            new SlotDef { Prop = "_DitherTex", Usage = TextureUsageKind.SpecialDeforming, UnsafeAlways = true },
            new SlotDef { Prop = "_DissolveMask", Usage = TextureUsageKind.Mask },
            new SlotDef { Prop = "_DissolveNoiseMask", Usage = TextureUsageKind.SpecialDeforming, UnsafeAlways = true },
            new SlotDef { Prop = "_FurNoiseMask", Usage = TextureUsageKind.SpecialDeforming, UnsafeAlways = true },
            new SlotDef { Prop = "_FurMask", Usage = TextureUsageKind.Mask, MainUv = true },
            new SlotDef { Prop = "_FurLengthMask", Usage = TextureUsageKind.Mask, MainUv = true },
            new SlotDef { Prop = "_AnisotropyTangentMap", Usage = TextureUsageKind.Normal, MainUv = true },
            new SlotDef { Prop = "_AnisotropyScaleMask", Usage = TextureUsageKind.Mask, MainUv = true },
            new SlotDef { Prop = "_BacklightColorTex", Usage = TextureUsageKind.Albedo, MainUv = true },
        };

        public static ShaderAnalysisResult Analyze(Material mat)
        {
            var r = new ShaderAnalysisResult { Supported = true };
            FillAlpha(mat, r);

            foreach (var def in Slots)
            {
                if (!mat.HasProperty(def.Prop)) continue;
                if (!string.IsNullOrEmpty(def.UseFlag) && mat.HasProperty(def.UseFlag) && mat.GetFloat(def.UseFlag) < 0.5f)
                    continue;
                var tex = mat.GetTexture(def.Prop) as Texture2D;
                if (tex == null) continue;

                var slot = new ShaderTextureSlot
                {
                    PropertyName = def.Prop,
                    Usage = def.Usage,
                    UvChannel = 0,
                    IsNormal = def.Usage == TextureUsageKind.Normal,
                    IsMask = def.Usage == TextureUsageKind.Mask,
                    IsGray = def.Usage == TextureUsageKind.Mask || def.Usage == TextureUsageKind.Gray,
                    ImpliedColorSpace = def.Usage == TextureUsageKind.Albedo || def.Usage == TextureUsageKind.Emission
                        ? ColorSpace.Gamma : ColorSpace.Linear
                };

                if (def.UnsafeAlways)
                {
                    slot.HasUnsafeTransform = true;
                    slot.UnsafeReason = "lilToon special/deforming slot (matcap/parallax/decal/noise)";
                }

                if (mat.HasProperty(def.Prop + "IsDecal") && mat.GetFloat(def.Prop + "IsDecal") > 0.5f)
                {
                    slot.HasUnsafeTransform = true;
                    slot.UnsafeReason = "decal";
                }

                var st = ShaderAnalyzeService.GetSt(mat, def.Prop);
                if (!ShaderAnalyzeService.IsIdentitySt(st))
                {
                    slot.HasUnsafeTransform = true;
                    slot.UnsafeReason = $"{def.Prop}_ST is not identity {st}";
                }

                var sr = def.Prop + "_ScrollRotate";
                if (mat.HasProperty(sr))
                {
                    var v = mat.GetVector(sr);
                    if (v.sqrMagnitude > 1e-8f)
                    {
                        slot.HasUnsafeTransform = true;
                        slot.UnsafeReason = "ScrollRotate";
                    }
                }

                var uvMode = ShaderAnalyzeService.ReadUvMode(mat, def.Prop);
                // lilToon UVMode: 0 UV0, 1 UV1, 2 UV2, 3 UV3, MatCap/Rim/Screen are higher or separate props.
                // lilToon UVMode：0=UV0 … 3=UV3。
                if (uvMode >= 0 && uvMode <= 3) slot.UvChannel = uvMode;
                if (uvMode > 3)
                {
                    slot.HasUnsafeTransform = true;
                    slot.UnsafeReason = "non-mesh UV mode " + uvMode;
                }

                r.Slots.Add(slot);
            }

            // Unknown extra Texture2D properties → generic, may whitelist. / 未列表的 Texture2D 走通用逻辑。
            GenericPropertyAnalyzer.AppendUnknown(mat, r, known: SlotsSet());
            return r;
        }

        private static HashSet<string> SlotsSet()
        {
            var s = new HashSet<string>();
            foreach (var d in Slots) s.Add(d.Prop);
            return s;
        }

        internal static void FillAlpha(Material mat, ShaderAnalysisResult r)
        {
            // lilToon _TransparentMode: 0 Opaque, 1 Cutout, 2 Transparent, 3 Refraction, 4 Fur, 5 FurCutout, 6 Gem...
            // 来自 lilEnumeration / shader inspector。
            if (mat.HasProperty("_TransparentMode"))
            {
                var m = Mathf.RoundToInt(mat.GetFloat("_TransparentMode"));
                if (m == 1 || m == 5) r.AlphaMode = AlphaEvalMode.Cutout;
                else if (m == 0) r.AlphaMode = AlphaEvalMode.Opaque;
                else r.AlphaMode = AlphaEvalMode.Blend;
            }
            else if (mat.IsKeywordEnabled("_ALPHATEST_ON") || mat.IsKeywordEnabled("_ALPHATEST"))
                r.AlphaMode = AlphaEvalMode.Cutout;
            else if (mat.IsKeywordEnabled("_ALPHABLEND_ON") || mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
                r.AlphaMode = AlphaEvalMode.Blend;
            else
            {
                var tag = mat.GetTag("RenderType", false, "");
                if (tag == "TransparentCutout") r.AlphaMode = AlphaEvalMode.Cutout;
                else if (tag == "Transparent") r.AlphaMode = AlphaEvalMode.Blend;
            }

            if (mat.HasProperty("_Cutoff")) r.Cutoff = mat.GetFloat("_Cutoff");
        }
    }

    public static class StandardKeywordAnalyzer
    {
        public static ShaderAnalysisResult Analyze(Material mat)
        {
            var r = new ShaderAnalysisResult { Supported = true };
            LilToonAnalyzer.FillAlpha(mat, r);
            TryAdd(mat, r, "_MainTex", TextureUsageKind.Albedo, 0);
            if (mat.IsKeywordEnabled("_NORMALMAP") || mat.HasProperty("_BumpMap"))
                TryAdd(mat, r, "_BumpMap", TextureUsageKind.Normal, 0);
            if (mat.IsKeywordEnabled("_METALLICGLOSSMAP") || mat.HasProperty("_MetallicGlossMap"))
                TryAdd(mat, r, "_MetallicGlossMap", TextureUsageKind.Mask, 0);
            if (mat.IsKeywordEnabled("_SPECGLOSSMAP"))
                TryAdd(mat, r, "_SpecGlossMap", TextureUsageKind.Mask, 0);
            if (mat.IsKeywordEnabled("_OCCLUSIONMAP") || mat.HasProperty("_OcclusionMap"))
                TryAdd(mat, r, "_OcclusionMap", TextureUsageKind.Gray, 0);
            if (mat.IsKeywordEnabled("_EMISSION") || mat.HasProperty("_EmissionMap"))
                TryAdd(mat, r, "_EmissionMap", TextureUsageKind.Emission, 0);
            if (mat.IsKeywordEnabled("_PARALLAXMAP"))
            {
                TryAdd(mat, r, "_ParallaxMap", TextureUsageKind.SpecialDeforming, 0, unsafeReason: "parallax");
            }
            if (mat.IsKeywordEnabled("_DETAIL_MULX2"))
            {
                TryAdd(mat, r, "_DetailAlbedoMap", TextureUsageKind.Albedo, 0);
                TryAdd(mat, r, "_DetailNormalMap", TextureUsageKind.Normal, 0);
            }
            GenericPropertyAnalyzer.AppendUnknown(mat, r, null);
            return r;
        }

        internal static void TryAdd(Material mat, ShaderAnalysisResult r, string prop, TextureUsageKind usage, int uv,
            string unsafeReason = null)
        {
            if (!mat.HasProperty(prop)) return;
            var tex = mat.GetTexture(prop) as Texture2D;
            if (tex == null) return;
            var slot = new ShaderTextureSlot
            {
                PropertyName = prop,
                Usage = usage,
                UvChannel = uv,
                IsNormal = usage == TextureUsageKind.Normal,
                IsMask = usage == TextureUsageKind.Mask,
                IsGray = usage == TextureUsageKind.Gray || usage == TextureUsageKind.Mask,
                ImpliedColorSpace = usage == TextureUsageKind.Albedo || usage == TextureUsageKind.Emission
                    ? ColorSpace.Gamma : ColorSpace.Linear
            };
            var st = ShaderAnalyzeService.GetSt(mat, prop);
            if (!ShaderAnalyzeService.IsIdentitySt(st))
            {
                slot.HasUnsafeTransform = true;
                slot.UnsafeReason = prop + "_ST not identity";
            }
            if (unsafeReason != null)
            {
                slot.HasUnsafeTransform = true;
                slot.UnsafeReason = unsafeReason;
            }
            r.Slots.Add(slot);
        }
    }

    public static class GenericPropertyAnalyzer
    {
        public static ShaderAnalysisResult Analyze(Material mat)
        {
            var r = new ShaderAnalysisResult { Supported = true };
            LilToonAnalyzer.FillAlpha(mat, r);
            AppendUnknown(mat, r, null);
            if (r.Slots.Count == 0)
            {
                r.Supported = false;
                r.UnsupportedReason = "no Texture2D slots found";
            }
            return r;
        }

        public static void AppendUnknown(Material mat, ShaderAnalysisResult r, HashSet<string> known)
        {
            var shader = mat.shader;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                var prop = shader.GetPropertyName(i);
                if (known != null && known.Contains(prop)) continue;
                if (r.Slots.Exists(s => s.PropertyName == prop)) continue;
                var tex = mat.GetTexture(prop);
                if (!(tex is Texture2D)) continue;
                var flags = shader.GetPropertyFlags(i);
                var usage = GuessUsage(prop);
                var slot = new ShaderTextureSlot
                {
                    PropertyName = prop,
                    Usage = usage,
                    UvChannel = ShaderAnalyzeService.ReadUvMode(mat, prop),
                    IsNormal = usage == TextureUsageKind.Normal || (flags & ShaderPropertyFlags.Normal) != 0,
                    IsMask = usage == TextureUsageKind.Mask,
                    IsGray = usage == TextureUsageKind.Gray || usage == TextureUsageKind.Mask,
                    ImpliedColorSpace = (flags & ShaderPropertyFlags.NonModifiableTextureData) != 0
                        ? ColorSpace.Linear
                        : (usage == TextureUsageKind.Albedo ? ColorSpace.Gamma : ColorSpace.Linear)
                };
                if (slot.UvChannel > 3)
                {
                    slot.HasUnsafeTransform = true;
                    slot.UnsafeReason = "UV mode > 3";
                }
                var st = ShaderAnalyzeService.GetSt(mat, prop);
                if (!ShaderAnalyzeService.IsIdentitySt(st))
                {
                    slot.HasUnsafeTransform = true;
                    slot.UnsafeReason = "non-identity ST";
                }
                var n = prop.ToLowerInvariant();
                if (n.Contains("matcap") || n.Contains("cube") || n.Contains("triplanar") || n.Contains("decal") ||
                    n.Contains("parallax") || n.Contains("noise") || n.Contains("dither") || n.Contains("lut") ||
                    n.Contains("toonramp") || n.Contains("shadowramp"))
                {
                    slot.HasUnsafeTransform = true;
                    slot.Usage = TextureUsageKind.SpecialDeforming;
                    slot.UnsafeReason = "heuristic special-purpose texture name";
                }
                r.Slots.Add(slot);
            }
        }

        private static TextureUsageKind GuessUsage(string prop)
        {
            var n = prop.ToLowerInvariant();
            if (n.Contains("bump") || n.Contains("normal")) return TextureUsageKind.Normal;
            if (n.Contains("mask") || n.Contains("metallic") || n.Contains("rough") || n.Contains("ao") ||
                n.Contains("occlusion") || n.Contains("smooth")) return TextureUsageKind.Mask;
            if (n.Contains("emission") || n.Contains("emissive") || n.Contains("glow")) return TextureUsageKind.Emission;
            if (n.Contains("main") || n.Contains("albedo") || n.Contains("base") || n.Contains("diffuse") ||
                n.Contains("color")) return TextureUsageKind.Albedo;
            return TextureUsageKind.Unknown;
        }
    }
}
