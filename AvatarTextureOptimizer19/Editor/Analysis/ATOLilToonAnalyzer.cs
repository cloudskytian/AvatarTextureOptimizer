// English: lilToon property / keyword table derived from lilToon 2.3.4 + AAO ShaderInformation.Liltoon.cs.
// 中文：基于 lilToon 2.3.4 与 AAO ShaderInformation.Liltoon.cs 的属性/关键字表。
using System.Collections.Generic;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;
using Net.Fosa.AvatarTextureOptimizer.API;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOLilToonAnalyzer
    {
        public static List<ATOTextureSlotInfo> Analyze(Material mat, ATOLogger log)
        {
            var list = new List<ATOTextureSlotInfo>();
            float cutoff;
            var alpha = ATOShaderAnalyzer.DetectAlphaMode(mat, out cutoff);

            // Main UV0 textures that follow _MainTex_ST
            Add(list, mat, "_MainTex", 0, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, false);
            Add(list, mat, "_BaseMap", 0, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, false);
            Add(list, mat, "_BaseColorMap", 0, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, false);
            Add(list, mat, "_MainColorAdjustMask", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
            Add(list, mat, "_AlphaMask", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);

            if (On(mat, "_UseBumpMap"))
                Add(list, mat, "_BumpMap", 0, ATOTextureSemantic.Normal, alpha, cutoff, false);
            if (On(mat, "_UseBump2ndMap"))
            {
                var uv = Int(mat, "_Bump2ndMap_UVMode", 0);
                Add(list, mat, "_Bump2ndMap", uv, ATOTextureSemantic.Normal, alpha, cutoff, uv > 3);
                Add(list, mat, "_Bump2ndScaleMask", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
            }

            if (On(mat, "_UseAnisotropy"))
            {
                Add(list, mat, "_AnisotropyTangentMap", 0, ATOTextureSemantic.Normal, alpha, cutoff, false);
                Add(list, mat, "_AnisotropyScaleMask", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
                Add(list, mat, "_AnisotropyShiftNoiseMask", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
            }

            if (On(mat, "_UseBacklight"))
                Add(list, mat, "_BacklightColorTex", 0, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, false);

            if (On(mat, "_UseShadow"))
            {
                Add(list, mat, "_ShadowStrengthMask", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
                Add(list, mat, "_ShadowBorderMask", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
                Add(list, mat, "_ShadowBlurMask", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
                var colorType = Int(mat, "_ShadowColorType", 0);
                var nonMesh = colorType == 1;
                Add(list, mat, "_ShadowColorTex", 0, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, nonMesh);
                Add(list, mat, "_Shadow2ndColorTex", 0, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, nonMesh);
                Add(list, mat, "_Shadow3rdColorTex", 0, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, nonMesh);
            }

            if (On(mat, "_UseRimShade"))
                Add(list, mat, "_RimShadeMask", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);

            if (On(mat, "_UseReflection"))
            {
                Add(list, mat, "_SmoothnessTex", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
                Add(list, mat, "_MetallicGlossMap", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
                Add(list, mat, "_ReflectionColorTex", 0, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, false);
            }

            if (On(mat, "_UseRim"))
                Add(list, mat, "_RimColorTex", 0, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, false);

            if (On(mat, "_UseGlitter"))
            {
                Add(list, mat, "_GlitterColorTex", 0, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, false);
                if (On(mat, "_GlitterApplyShape"))
                    Add(list, mat, "_GlitterShapeTex", 0, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, true);
            }

            AddEmission(list, mat, "_UseEmission", "_EmissionMap", "_EmissionMap_UVMode", "_EmissionBlendMask", alpha, cutoff);
            AddEmission(list, mat, "_UseEmission2nd", "_Emission2ndMap", "_Emission2ndMap_UVMode", "_Emission2ndBlendMask", alpha, cutoff);

            if (On(mat, "_UseMain2ndTex"))
                AddSubTex(list, mat, "_Main2ndTex", "_Main2ndTex_UVMode", "_Main2ndBlendMask", alpha, cutoff);
            if (On(mat, "_UseMain3rdTex"))
                AddSubTex(list, mat, "_Main3rdTex", "_Main3rdTex_UVMode", "_Main3rdBlendMask", alpha, cutoff);

            if (On(mat, "_UseOutline") || (mat.shader != null && mat.shader.name.IndexOf("Outline", System.StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Add(list, mat, "_OutlineTex", 0, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, false);
                Add(list, mat, "_OutlineWidthMask", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
                var uv = Int(mat, "_OutlineVectorUVMode", 0);
                Add(list, mat, "_OutlineVectorTex", uv, ATOTextureSemantic.Normal, alpha, cutoff, uv > 3);
            }

            Add(list, mat, "_FurNoiseMask", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
            Add(list, mat, "_FurMask", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
            Add(list, mat, "_FurLengthMask", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
            Add(list, mat, "_FurVectorTex", 0, ATOTextureSemantic.Normal, alpha, cutoff, false);

            // Always special / non-mesh — mark so eligibility filter can skip.
            Add(list, mat, "_MatCapTex", 0, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, true);
            Add(list, mat, "_MatCap2ndTex", 0, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, true);
            Add(list, mat, "_DitherTex", 0, ATOTextureSemantic.Gray, alpha, cutoff, true);
            Add(list, mat, "_MainGradationTex", 0, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, true);
            Add(list, mat, "_ParallaxMap", 0, ATOTextureSemantic.Gray, alpha, cutoff, true);

            if (On(mat, "_UseMatCap"))
            {
                Add(list, mat, "_MatCapBlendMask", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
                if (On(mat, "_MatCapCustomNormal"))
                    Add(list, mat, "_MatCapBumpMap", 0, ATOTextureSemantic.Normal, alpha, cutoff, false);
            }

            if (On(mat, "_UseMatCap2nd"))
            {
                Add(list, mat, "_MatCap2ndBlendMask", 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
                if (On(mat, "_MatCap2ndCustomNormal"))
                    Add(list, mat, "_MatCap2ndBumpMap", 0, ATOTextureSemantic.Normal, alpha, cutoff, false);
            }

            log.VerboseInfo("lilToon analyzer produced " + list.Count + " slots for " + mat.name);
            return list;
        }

        private static void AddEmission(List<ATOTextureSlotInfo> list, Material mat, string use, string map, string uvMode,
            string mask, ATOAlphaMode alpha, float cutoff)
        {
            if (!On(mat, use)) return;
            var uv = Int(mat, uvMode, 0);
            Add(list, mat, map, uv, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, uv > 3);
            Add(list, mat, mask, 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
        }

        private static void AddSubTex(List<ATOTextureSlotInfo> list, Material mat, string tex, string uvMode, string mask,
            ATOAlphaMode alpha, float cutoff)
        {
            var uv = Int(mat, uvMode, 0);
            var special = uv > 3 || ATOShaderAnalyzer.HasNonIdentityST(mat, tex);
            // Decal / scroll / copy flags make the UV non-static.
            if (On(mat, tex + "IsDecal") || On(mat, tex + "ShouldCopy") || On(mat, tex + "ShouldFlipMirror"))
                special = true;
            Add(list, mat, tex, uv, ATOTextureSemantic.AlbedoOpaque, alpha, cutoff, special);
            Add(list, mat, mask, 0, ATOTextureSemantic.Gray, alpha, cutoff, false);
        }

        private static void Add(List<ATOTextureSlotInfo> list, Material mat, string prop, int uv,
            ATOTextureSemantic semantic, ATOAlphaMode alpha, float cutoff, bool special)
        {
            if (mat == null || !mat.HasProperty(prop)) return;
            var tex = mat.GetTexture(prop) as Texture2D;
            if (tex == null) return;

            if (semantic == ATOTextureSemantic.AlbedoOpaque && alpha != ATOAlphaMode.Opaque)
                semantic = ATOTextureSemantic.AlbedoTransparent;

            var slot = new ATOTextureSlotInfo
            {
                Material = mat,
                PropertyName = prop,
                Texture = tex,
                UvChannel = special ? -1 : Mathf.Clamp(uv, 0, 7),
                HasTransform = ATOShaderAnalyzer.HasNonIdentityST(mat, prop),
                IsMeshSampled = !special && uv >= 0 && uv <= 7,
                IsSpecialPurpose = special,
                Semantic = semantic == ATOTextureSemantic.Normal
                    ? ATOTextureSemantic.Normal
                    : ATOShaderAnalyzer.GuessSemantic(prop, tex, alpha) == ATOTextureSemantic.Normal
                        ? ATOTextureSemantic.Normal
                        : semantic,
                AlphaMode = alpha,
                Cutoff = cutoff,
                WrapMode = tex.wrapMode,
                FilterMode = tex.filterMode,
                LinearColorSpace = ATOTextureCache.IsLinearAsset(tex)
            };
            list.Add(slot);
        }

        private static bool On(Material mat, string prop)
        {
            if (mat == null || string.IsNullOrEmpty(prop) || !mat.HasProperty(prop)) return false;
            return mat.GetFloat(prop) > 0.5f;
        }

        private static int Int(Material mat, string prop, int fallback)
        {
            if (mat == null || !mat.HasProperty(prop)) return fallback;
            return Mathf.RoundToInt(mat.GetFloat(prop));
        }
    }
}
