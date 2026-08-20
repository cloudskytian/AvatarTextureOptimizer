using System;
using Net.Fosa.AvatarTextureOptimizer;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class IslandScaler
    {
        public static void ScaleAll(System.Collections.Generic.List<UvGroup> groups, AtoPlatformSettings settings, BakeReport report)
        {
            using (AtoLog.Time("Island scale"))
            {
                foreach (var g in groups)
                {
                    if (g.Whitelisted) continue;
                    foreach (var isl in g.Islands)
                        ScaleIsland(g, isl, settings);
                }
            }
        }

        static void ScaleIsland(UvGroup g, UvIsland isl, AtoPlatformSettings s)
        {
            var q = s.QualityParameters;
            if (s.QualityPreset != AtoQualityPreset.Custom && q.IsNearLossless ||
                s.QualityPreset == AtoQualityPreset.Custom && q.IsNearLossless)
            {
                isl.ScaleU = isl.ScaleV = 1f;
                return;
            }

            var tex = g.Textures.Count > 0 ? g.Textures[0] : null;
            if (tex == null || !tex.isReadable)
            {
                DensityClamp(g, isl, s, tex);
                return;
            }

            Color[] orig;
            try
            {
                orig = SafeRead(tex, isl.PixelBounds);
            }
            catch
            {
                DensityClamp(g, isl, s, tex);
                return;
            }

            if (QualityMetrics.IsSolid(orig) && !q.IsNearLossless)
            {
                int min = Mathf.Min(4, Mathf.Min(isl.PixelBounds.width, isl.PixelBounds.height));
                float f = min / (float)Mathf.Max(1, Mathf.Min(isl.PixelBounds.width, isl.PixelBounds.height));
                isl.ScaleU = isl.ScaleV = f;
                isl.IsSolidColor = true;
                isl.SolidColor = orig[0];
                return;
            }

            // uniform binary search then anisotropic refine
            float lo = 0.05f, hi = 1f, best = 1f;
            for (int i = 0; i < 8; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Pass(orig, isl.PixelBounds, mid, mid, g, q))
                {
                    best = mid;
                    hi = mid;
                }
                else lo = mid;
            }
            isl.ScaleU = isl.ScaleV = best;

            float loU = 0.05f, hiU = best, bestU = best;
            for (int i = 0; i < 6; i++)
            {
                float mid = (loU + hiU) * 0.5f;
                if (Pass(orig, isl.PixelBounds, mid, isl.ScaleV, g, q)) { bestU = mid; hiU = mid; }
                else loU = mid;
            }
            isl.ScaleU = bestU;
            float loV = 0.05f, hiV = best, bestV = best;
            for (int i = 0; i < 6; i++)
            {
                float mid = (loV + hiV) * 0.5f;
                if (Pass(orig, isl.PixelBounds, isl.ScaleU, mid, g, q)) { bestV = mid; hiV = mid; }
                else loV = mid;
            }
            isl.ScaleV = bestV;

            DensityClamp(g, isl, s, tex);
            // barrel: max size among UV group textures
            AtoLog.VerboseInfo($"Island {g.Id} scale=({isl.ScaleU:F3},{isl.ScaleV:F3}) px={isl.PixelBounds}");
        }

        static bool Pass(Color[] orig, RectInt pb, float su, float sv, UvGroup g, AtoQualityParameters q)
        {
            int dw = Mathf.Max(1, Mathf.RoundToInt(pb.width * su));
            int dh = Mathf.Max(1, Mathf.RoundToInt(pb.height * sv));
            var small = QualityMetrics.PremultipliedDownsample(orig, pb.width, pb.height, dw, dh);
            var up = QualityMetrics.BilinearUpsample(small, dw, dh, pb.width, pb.height);
            foreach (var sem in g.Semantics)
            {
                if (sem == AtoTextureSemantic.Normal)
                {
                    float mean = QualityMetrics.NormalAngleMeanP95(orig, up, out float p95);
                    if (mean > q.NormalAngleDegMax || p95 > q.NormalP95DegMax) return false;
                    continue;
                }
                if (sem == AtoTextureSemantic.Gray || sem == AtoTextureSemantic.Mask || sem == AtoTextureSemantic.MetallicGloss)
                {
                    if (QualityMetrics.ChannelRmse(orig, up, true, true, true, false) > q.GrayRmseMax) return false;
                    continue;
                }
                float ssim = QualityMetrics.MsSsim(orig, up, pb.width, pb.height);
                if (ssim < q.MsSsimMin) return false;
                float de = 0;
                int step = Mathf.Max(1, orig.Length / 256);
                for (int i = 0; i < orig.Length; i += step)
                    de = Mathf.Max(de, QualityMetrics.Ciede2000(orig[i], up[i]));
                if (de > q.Ciede2000Max) return false;
                if (g.StrictestAlpha == AtoAlphaMode.Blend && QualityMetrics.AlphaRmse(orig, up) > q.AlphaRmseMax)
                    return false;
                if (g.StrictestAlpha == AtoAlphaMode.Cutout && QualityMetrics.CutoutIou(orig, up, g.StrictestCutoff) < q.CutoutIouMin)
                    return false;
            }
            return true;
        }

        static void DensityClamp(UvGroup g, UvIsland isl, AtoPlatformSettings s, Texture2D tex)
        {
            float meters = Mathf.Sqrt(Mathf.Max(isl.WorldArea, 1e-8f));
            float minPx = (int)s.MinPixelDensity * meters;
            float maxPx = (int)s.MaxPixelDensity * meters;
            float shortEdge = Mathf.Min(isl.PixelBounds.width * isl.ScaleU, isl.PixelBounds.height * isl.ScaleV);
            if (shortEdge < minPx && meters > 1e-4f)
            {
                float k = minPx / Mathf.Max(shortEdge, 1e-4f);
                isl.ScaleU = Mathf.Min(1f, isl.ScaleU * k);
                isl.ScaleV = Mathf.Min(1f, isl.ScaleV * k);
            }
            if (shortEdge > maxPx && meters > 1e-4f)
            {
                float k = maxPx / shortEdge;
                isl.ScaleU *= k;
                isl.ScaleV *= k;
            }
            isl.ScaleU = Mathf.Clamp01(isl.ScaleU);
            isl.ScaleV = Mathf.Clamp01(isl.ScaleV);
        }

        static Color[] SafeRead(Texture2D tex, RectInt r)
        {
            r.x = Mathf.Clamp(r.x, 0, tex.width - 1);
            r.y = Mathf.Clamp(r.y, 0, tex.height - 1);
            r.width = Mathf.Clamp(r.width, 1, tex.width - r.x);
            r.height = Mathf.Clamp(r.height, 1, tex.height - r.y);
            return tex.GetPixels(r.x, r.y, r.width, r.height);
        }
    }
}
