using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class AtoIslandPipeline
    {
        public static List<AtoIsland> Process(AtoGraph graph, AtoPlatformOverride settings,
            AtoTextureCache cache, AtoReport report, CancellationProbe cancel)
        {
            var all = new List<AtoIsland>();
            int id = 0;
            var processed = new HashSet<(Mesh, int, int, Texture2D)>();

            foreach (var b in graph.Bindings.Where(x => x.Eligible && x.Mesh != null && x.Texture != null))
            {
                cancel.ThrowIfCancelled();
                var key = (b.Mesh, b.Submesh, b.UvChannel, b.Texture);
                if (!processed.Add(key)) continue;

                var islands = AtoIslandExtractor.Extract(b.Mesh, b.Submesh, b.UvChannel, b.Texture);
                foreach (var isl in islands)
                {
                    isl.Id = ++id;
                    isl.Role = b.Role;
                    isl.Blend = b.Blend;
                    isl.Cutoff = b.Cutoff;
                    isl.UvGroupId = FindUvGroup(graph, b);
                    if (graph.TypeGroup.TryGetValue(b.Texture, out var tk)) isl.TypeKey = tk;
                    isl.WorldArea = AtoWorldArea.IslandArea(b.Renderer, b.Mesh, isl, b.Renderer.transform.root.gameObject);
                    ScaleIsland(isl, settings, cache, report);
                    all.Add(isl);
                }
            }

            // UV-group barrel: same scale for all textures sharing a UV.
            ApplyUvGroupBarrel(all, settings, cache, report);
            return all;
        }

        static int FindUvGroup(AtoGraph g, AtoBinding b)
        {
            foreach (var ug in g.UvGroups.Values)
                if (ug.Bindings.Contains(b)) return ug.Id;
            return 0;
        }

        static void ScaleIsland(AtoIsland isl, AtoPlatformOverride settings, AtoTextureCache cache, AtoReport report)
        {
            var tex = isl.Source;
            var px = cache.GetPixels(tex);
            int x0 = Mathf.Clamp(Mathf.FloorToInt(isl.PixelBounds.xMin), 0, tex.width - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(isl.PixelBounds.yMin), 0, tex.height - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(isl.PixelBounds.xMax), x0 + 1, tex.width);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(isl.PixelBounds.yMax), y0 + 1, tex.height);
            int bw = x1 - x0, bh = y1 - y0;
            int shortSide = Mathf.Min(bw, bh);

            isl.SolidColor = AtoQualityEval.IsSolid(px, x0, y0, x1, y1, tex.width, out isl.Solid);

            var q = settings.quality;
            if (q.targetQuality >= 0.999f)
            {
                isl.ScaleU = isl.ScaleV = 1f;
                report.Detail($"island {isl.Id} {tex.name} q=1 skip scale {bw}x{bh}");
                return;
            }

            if (isl.SolidColor)
            {
                int minS = Mathf.Min(4, shortSide);
                isl.ScaleU = isl.ScaleV = minS / (float)Mathf.Max(1, shortSide);
                report.Detail($"island {isl.Id} solid -> {minS}px");
                return;
            }

            // Density clamp. / 像素密度钳制。
            float world = Mathf.Max(isl.WorldArea, 1e-8f);
            float worldEdge = Mathf.Sqrt(world);
            float minPx = (int)settings.minPixelDensity * worldEdge;
            float maxPx = (int)settings.maxPixelDensity * worldEdge;
            float densityScale = 1f;
            if (shortSide > maxPx && maxPx > 1f) densityScale = maxPx / shortSide;
            if (shortSide * densityScale < minPx && shortSide > 0)
                densityScale = Mathf.Min(1f, minPx / shortSide);

            // Uniform binary search then anisotropic refine.
            float lo = 1f / Mathf.Max(shortSide, 1), hi = densityScale;
            for (int i = 0; i < 10; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (EvaluateScale(isl, px, tex, mid, mid, q, cache)) hi = mid;
                else lo = mid;
            }
            isl.ScaleU = isl.ScaleV = hi;

            // Anisotropic refine. / 双轴细化。
            float uLo = 1f / Mathf.Max(bw, 1), uHi = isl.ScaleU;
            for (int i = 0; i < 6; i++)
            {
                float mid = (uLo + uHi) * 0.5f;
                if (EvaluateScale(isl, px, tex, mid, isl.ScaleV, q, cache)) uHi = mid;
                else uLo = mid;
            }
            isl.ScaleU = uHi;
            float vLo = 1f / Mathf.Max(bh, 1), vHi = isl.ScaleV;
            for (int i = 0; i < 6; i++)
            {
                float mid = (vLo + vHi) * 0.5f;
                if (EvaluateScale(isl, px, tex, isl.ScaleU, mid, q, cache)) vHi = mid;
                else vLo = mid;
            }
            isl.ScaleV = vHi;

            report.OriginalTexels += (long)bw * bh;
            report.Detail($"island {isl.Id} {tex.name} scale=({isl.ScaleU:F3},{isl.ScaleV:F3}) src={bw}x{bh}");
        }

        static bool EvaluateScale(AtoIsland isl, Color32[] px, Texture2D tex,
            float su, float sv, AtoQualityParameters q, AtoTextureCache cache)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(isl.PixelBounds.xMin), 0, tex.width - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(isl.PixelBounds.yMin), 0, tex.height - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(isl.PixelBounds.xMax), x0 + 1, tex.width);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(isl.PixelBounds.yMax), y0 + 1, tex.height);
            int bw = x1 - x0, bh = y1 - y0;
            int dw = Mathf.Max(1, Mathf.RoundToInt(bw * su));
            int dh = Mathf.Max(1, Mathf.RoundToInt(bh * sv));
            var crop = new Color32[bw * bh];
            for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
                crop[y * bw + x] = px[(y0 + y) * tex.width + (x0 + x)];
            bool premul = isl.Blend != AtoBlendMode.Opaque;
            Color32[] down;
            if (dw * dh >= 64 && SystemInfo.supportsComputeShaders)
            {
                // GPU blit downsample of the island crop. / GPU 缩小岛裁剪。
                var tmp = new Texture2D(bw, bh, TextureFormat.RGBA32, false, true);
                tmp.SetPixels32(crop);
                tmp.Apply(false, false);
                down = AtoGpuQuality.GpuDownsample(tmp, dw, dh, true);
                Object.DestroyImmediate(tmp);
            }
            else
                down = AtoQualityEval.BilinearDownsample(crop, bw, bh, dw, dh, premul);
            if (isl.Role == AtoTextureRole.Normal)
                Renormalize(down);
            var score = AtoQualityEval.Compare(px, tex.width, tex.height, isl.PixelBounds, down, dw, dh,
                isl.Role, isl.Blend, isl.Cutoff, isl.Role == AtoTextureRole.Albedo);
            return AtoQualityEval.Passes(score, q, isl.Role, isl.Blend, Mathf.Min(bw, bh));
        }

        static void Renormalize(Color32[] px)
        {
            for (int i = 0; i < px.Length; i++)
            {
                var n = new Vector3(px[i].r / 255f * 2 - 1, px[i].g / 255f * 2 - 1, px[i].b / 255f * 2 - 1);
                if (n.sqrMagnitude < 1e-8f) n = Vector3.forward;
                n.Normalize();
                px[i] = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt((n.x * 0.5f + 0.5f) * 255), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt((n.y * 0.5f + 0.5f) * 255), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt((n.z * 0.5f + 0.5f) * 255), 0, 255),
                    px[i].a);
            }
        }

        static void ApplyUvGroupBarrel(List<AtoIsland> all, AtoPlatformOverride settings,
            AtoTextureCache cache, AtoReport report)
        {
            foreach (var grp in all.GroupBy(i => i.UvGroupId))
            {
                float maxU = 0, maxV = 0;
                int maxW = 1, maxH = 1;
                foreach (var i in grp)
                {
                    maxU = Mathf.Max(maxU, i.ScaleU);
                    maxV = Mathf.Max(maxV, i.ScaleV);
                    maxW = Mathf.Max(maxW, Mathf.CeilToInt(i.PixelBounds.width));
                    maxH = Mathf.Max(maxH, Mathf.CeilToInt(i.PixelBounds.height));
                }
                foreach (var i in grp)
                {
                    i.ScaleU = Mathf.Min(1f, maxU);
                    i.ScaleV = Mathf.Min(1f, maxV);
                    // Clamp not larger than largest original in UV group.
                    int tw = Mathf.Max(1, Mathf.RoundToInt(i.PixelBounds.width * i.ScaleU));
                    int th = Mathf.Max(1, Mathf.RoundToInt(i.PixelBounds.height * i.ScaleV));
                    if (tw > maxW) i.ScaleU *= maxW / (float)tw;
                    if (th > maxH) i.ScaleV *= maxH / (float)th;
                }
            }
        }
    }
}
