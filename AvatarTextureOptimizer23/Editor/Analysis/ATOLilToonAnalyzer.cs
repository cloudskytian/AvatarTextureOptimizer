using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// lilToon-aware analyzer. Property names and UV rules come from lilToon 2.3.4 source
    /// (ShaderInformation.Liltoon.cs + lilEnumeration.cs). Unknown future properties fall back
    /// to the generic analyzer; if we cannot prove mesh-UV sampling we mark ineligible.
    /// lilToon 专用分析。属性名和 UV 规则来自 2.3.4 源码。未知的未来属性回退通用分析；
    /// 无法证明是网格 UV 采样则判不合格。
    /// </summary>
    internal static class ATOLilToonAnalyzer
    {
        public static bool IsLilToon(Material mat)
        {
            if (mat == null || mat.shader == null) return false;
            var n = mat.shader.name;
            return n.IndexOf("lilToon", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Hidden/ltspass", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("_lil/", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   mat.HasProperty("_lilToonVersion");
        }

        public static bool TryAnalyze(Material mat, string prop, out ATOTextureSlotInfo info)
        {
            info = ATOGenericShaderAnalyzer.Analyze(mat, prop);

            // Main UV is UV0 unless _ShiftBackfaceUV != 0 (face-dependent — unsafe).
            // 主 UV 默认 UV0；_ShiftBackfaceUV 非 0 时随正反面变化，不安全。
            if (mat.HasProperty("_ShiftBackfaceUV") && mat.GetFloat("_ShiftBackfaceUV") != 0f &&
                IsMainFamily(prop))
            {
                info.eligible = false;
                info.ineligibleReason = "_ShiftBackfaceUV != 0 (face-dependent UV)";
                return true;
            }

            switch (prop)
            {
                case "_MainTex":
                case "_BaseMap":
                case "_BaseColorMap":
                case "_MainColorAdjustMask":
                case "_AlphaMask":
                case "_OutlineTex":
                case "_OutlineWidthMask":
                    info.uvChannel = 0;
                    info.category = prop == "_MainTex" || prop == "_BaseMap" || prop == "_BaseColorMap"
                        ? CategoryFromLilRenderMode(mat)
                        : ATOTextureCategory.Gray;
                    break;

                case "_BumpMap":
                    info.category = ATOTextureCategory.Normal;
                    info.uvChannel = 0;
                    if (mat.HasProperty("_UseBumpMap") && mat.GetFloat("_UseBumpMap") == 0f)
                    {
                        info.eligible = false;
                        info.ineligibleReason = "_UseBumpMap == 0";
                    }
                    break;

                case "_Bump2ndMap":
                    info.category = ATOTextureCategory.Normal;
                    info.uvChannel = ReadUvMode(mat, "_Bump2ndMap_UVMode", 0);
                    if (info.uvChannel < 0)
                    {
                        info.eligible = false;
                        info.ineligibleReason = "_Bump2ndMap UV mode unknown / non-mesh";
                    }
                    break;

                case "_EmissionMap":
                    info.uvChannel = ReadUvMode(mat, "_EmissionMap_UVMode", 0);
                    info.category = ATOTextureCategory.OpaqueAlbedo;
                    if (info.uvChannel < 0 || HasParallax(mat, "_EmissionParallaxDepth"))
                    {
                        info.eligible = false;
                        info.ineligibleReason = "emission UV non-mesh or parallax";
                    }
                    break;

                case "_Emission2ndMap":
                    info.uvChannel = ReadUvMode(mat, "_Emission2ndMap_UVMode", 0);
                    info.category = ATOTextureCategory.OpaqueAlbedo;
                    if (info.uvChannel < 0 || HasParallax(mat, "_Emission2ndParallaxDepth"))
                    {
                        info.eligible = false;
                        info.ineligibleReason = "emission2nd UV non-mesh or parallax";
                    }
                    break;

                case "_Main2ndTex":
                case "_Main3rdTex":
                    // Decal / MatCap / scroll — special purpose. / 贴花 / MatCap / 滚动 — 特殊用途。
                    info.eligible = false;
                    info.isSpecialPurpose = true;
                    info.ineligibleReason = "lilToon 2nd/3rd layer (decal/matcap/animated)";
                    break;

                case "_MatCapTex":
                case "_MatCap2ndTex":
                case "_MainGradationTex":
                case "_DitherTex":
                case "_GlitterShapeTex":
                case "_EmissionGradTex":
                case "_Emission2ndGradTex":
                case "_AudioLinkLocalMap":
                case "_ParallaxMap":
                    info.eligible = false;
                    info.isSpecialPurpose = true;
                    info.ineligibleReason = "lilToon non-mesh UV / special map " + prop;
                    break;
            }

            info.alphaMode = ATOGenericShaderAnalyzer.GuessAlphaMode(mat);
            info.cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f;
            return true;
        }

        private static bool IsMainFamily(string prop)
        {
            return prop == "_MainTex" || prop == "_BaseMap" || prop == "_BaseColorMap" ||
                   prop == "_BumpMap" || prop == "_AlphaMask";
        }

        private static bool HasParallax(Material mat, string prop)
        {
            return mat.HasProperty(prop) && Mathf.Abs(mat.GetFloat(prop)) > 1e-6f;
        }

        /// <summary>
        /// lilToon UVMode: 0-3 = UV0-3, 4 = MatCap/Rim/NonMesh. Returns -1 if unknown/non-mesh.
        /// lilToon UVMode：0-3 = UV0-3，4 = MatCap/边缘/非网格。未知或非网格返回 -1。
        /// </summary>
        private static int ReadUvMode(Material mat, string prop, int fallback)
        {
            if (!mat.HasProperty(prop)) return fallback;
            var v = Mathf.RoundToInt(mat.GetFloat(prop));
            if (v >= 0 && v <= 3) return v;
            return -1;
        }

        private static ATOTextureCategory CategoryFromLilRenderMode(Material mat)
        {
            var mode = ATOGenericShaderAnalyzer.GuessAlphaMode(mat);
            return mode == ATOAlphaMode.Opaque
                ? ATOTextureCategory.OpaqueAlbedo
                : ATOTextureCategory.TransparentAlbedo;
        }
    }
}
