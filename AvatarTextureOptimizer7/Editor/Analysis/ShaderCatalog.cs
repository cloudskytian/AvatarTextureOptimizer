using System;
using System.Collections.Generic;
using Fosa.AvatarTextureOptimizer;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Known texture properties from Unity / lilToon / Poiyomi / UTS, verified against those repos.
    /// 来自 Unity / lilToon / Poiyomi / UTS 的已知贴图属性（对照仓库核实）。
    /// </summary>
    public static class ShaderCatalog
    {
        public struct Info
        {
            public AtoTextureKind Kind;
            public bool OftenNonMesh;
            public bool[] DefaultUsedChannels;
        }

        static readonly Dictionary<string, Info> Map;

        static ShaderCatalog()
        {
            Map = new Dictionary<string, Info>(StringComparer.Ordinal);
            void Add(string n, AtoTextureKind k, bool nonMesh = false, bool[] ch = null)
            {
                if (!Map.ContainsKey(n)) Map[n] = new Info { Kind = k, OftenNonMesh = nonMesh, DefaultUsedChannels = ch };
            }

            // Unity
            Add("_MainTex", AtoTextureKind.Albedo);
            Add("_BaseMap", AtoTextureKind.Albedo);
            Add("_BaseColorMap", AtoTextureKind.Albedo);
            Add("_BumpMap", AtoTextureKind.Normal);
            Add("_DetailNormalMap", AtoTextureKind.Normal);
            Add("_NormalMap", AtoTextureKind.Normal);
            Add("_EmissionMap", AtoTextureKind.Albedo);
            Add("_MetallicGlossMap", AtoTextureKind.Mask, false, new[] { true, false, false, true });
            Add("_SpecGlossMap", AtoTextureKind.Mask);
            Add("_OcclusionMap", AtoTextureKind.Gray, false, new[] { true, false, false, false });
            Add("_ParallaxMap", AtoTextureKind.Gray, false, new[] { true, false, false, false });
            Add("_DetailMask", AtoTextureKind.Gray);
            Add("_DetailAlbedoMap", AtoTextureKind.Albedo);
            Add("_MaskMap", AtoTextureKind.Mask);

            // lilToon (from AAO LiltoonShaderInformation + lilToon 2.3.4)
            Add("_MainColorAdjustMask", AtoTextureKind.Gray);
            Add("_Main2ndTex", AtoTextureKind.Albedo);
            Add("_Main2ndBlendMask", AtoTextureKind.Gray);
            Add("_Main2ndDissolveMask", AtoTextureKind.Gray);
            Add("_Main2ndDissolveNoiseMask", AtoTextureKind.Gray);
            Add("_Main3rdTex", AtoTextureKind.Albedo);
            Add("_Main3rdBlendMask", AtoTextureKind.Gray);
            Add("_Main3rdDissolveMask", AtoTextureKind.Gray);
            Add("_Main3rdDissolveNoiseMask", AtoTextureKind.Gray);
            Add("_AlphaMask", AtoTextureKind.Gray);
            Add("_Bump2ndMap", AtoTextureKind.Normal);
            Add("_Bump2ndScaleMask", AtoTextureKind.Gray);
            Add("_AnisotropyTangentMap", AtoTextureKind.Normal);
            Add("_AnisotropyScaleMask", AtoTextureKind.Gray);
            Add("_AnisotropyShiftNoiseMask", AtoTextureKind.Gray);
            Add("_BacklightColorTex", AtoTextureKind.Albedo);
            Add("_ShadowStrengthMask", AtoTextureKind.Gray);
            Add("_ShadowBorderMask", AtoTextureKind.Gray);
            Add("_ShadowBlurMask", AtoTextureKind.Gray);
            Add("_ShadowColorTex", AtoTextureKind.Albedo);
            Add("_Shadow2ndColorTex", AtoTextureKind.Albedo);
            Add("_Shadow3rdColorTex", AtoTextureKind.Albedo);
            Add("_RimShadeMask", AtoTextureKind.Gray);
            Add("_SmoothnessTex", AtoTextureKind.Gray);
            Add("_ReflectionColorTex", AtoTextureKind.Albedo);
            Add("_MatCapTex", AtoTextureKind.Albedo, true);
            Add("_MatCapBlendMask", AtoTextureKind.Gray);
            Add("_MatCapBumpMap", AtoTextureKind.Normal);
            Add("_MatCap2ndTex", AtoTextureKind.Albedo, true);
            Add("_MatCap2ndBlendMask", AtoTextureKind.Gray);
            Add("_MatCap2ndBumpMap", AtoTextureKind.Normal);
            Add("_RimColorTex", AtoTextureKind.Albedo);
            Add("_GlitterColorTex", AtoTextureKind.Albedo);
            Add("_GlitterShapeTex", AtoTextureKind.Albedo, true);
            Add("_EmissionBlendMask", AtoTextureKind.Gray);
            Add("_EmissionGradTex", AtoTextureKind.Albedo, true);
            Add("_Emission2ndMap", AtoTextureKind.Albedo);
            Add("_Emission2ndBlendMask", AtoTextureKind.Gray);
            Add("_Emission2ndGradTex", AtoTextureKind.Albedo, true);
            Add("_OutlineTex", AtoTextureKind.Albedo);
            Add("_OutlineWidthMask", AtoTextureKind.Gray);
            Add("_OutlineVectorTex", AtoTextureKind.Normal);
            Add("_FurNoiseMask", AtoTextureKind.Gray);
            Add("_FurMask", AtoTextureKind.Gray);
            Add("_FurLengthMask", AtoTextureKind.Gray);
            Add("_FurVectorTex", AtoTextureKind.Normal);
            Add("_AudioLinkMask", AtoTextureKind.Gray);
            Add("_AudioLinkLocalMap", AtoTextureKind.Gray, true);
            Add("_DissolveMask", AtoTextureKind.Gray);
            Add("_DissolveNoiseMask", AtoTextureKind.Gray);
            Add("_DitherTex", AtoTextureKind.Gray, true);
            Add("_MainGradationTex", AtoTextureKind.Albedo, true);
            Add("_TriMask", AtoTextureKind.Mask);
        }

        public static bool TryGet(string property, out Info info) => Map.TryGetValue(property, out info);

        public static AtoTextureKind GuessKind(string property, UnityEngine.Texture2D tex)
        {
            if (TryGet(property, out var info)) return info.Kind;
            var n = property ?? "";
            if (n.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0)
                return AtoTextureKind.Normal;
            if (n.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Metallic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Occlusion", StringComparison.OrdinalIgnoreCase) >= 0)
                return AtoTextureKind.Mask;
            return AtoTextureKind.Albedo;
        }

        public static bool IsLikelyNonMesh(string property)
        {
            return TryGet(property, out var info) && info.OftenNonMesh;
        }
    }
}
