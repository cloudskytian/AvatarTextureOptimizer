using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public sealed class AtoAtlasResult
    {
        public Texture2D Texture;
        public AtoTypeGroupKey Key;
        public AtoTextureRole Role;
        public int Width, Height;
        public float Utilization;
        public readonly List<AtoIsland> Islands = new List<AtoIsland>();
        public readonly List<Texture2D> Sources = new List<Texture2D>();
    }

    /// <summary>
    /// Pack by type-group queues; atomic unit is a UV-group (shared layout across roles).
    /// 按类型组排队；装箱原子为 UV 组（各角色图集共用同一套岛坐标）。
    /// </summary>
    public static class AtoAtlasPipeline
    {
        public static List<AtoAtlasResult> Pack(
            AtoGraph graph, List<AtoIsland> islands, AtoPlatformOverride settings,
            AtoPlatform platform, AtoTextureCache cache, AtoReport report, CancellationProbe cancel)
        {
            var results = new List<AtoAtlasResult>();
            var eligible = islands.Where(i => i.Eligible && i.Source != null &&
                                              !graph.WhitelistedTextures.Contains(i.Source)).ToList();

            foreach (var isl in eligible)
            {
                if (isl.Mask == null)
                    AtoBitmaskPacker.Rasterize(isl, isl.Mesh);
            }

            foreach (var tg in eligible.GroupBy(i => i.TypeKey))
            {
                cancel.ThrowIfCancelled();
                // Atomic: UV groups sorted by raster area desc. / UV 组按光栅面积降序。
                var uvAtoms = tg.GroupBy(i => i.UvGroupId)
                    .OrderByDescending(g => g.Sum(AtoBitmaskPacker.OccupiedArea))
                    .ToList();

                var queues = new List<List<IGrouping<int, AtoIsland>>> { new List<IGrouping<int, AtoIsland>>() };
                foreach (var atom in uvAtoms)
                {
                    bool placed = false;
                    foreach (var q in queues)
                    {
                        var trial = q.Concat(new[] { atom }).ToList();
                        if (TryPackQueue(trial, settings, platform, cache, report, out _))
                        {
                            q.Add(atom);
                            placed = true;
                            break;
                        }
                    }
                    if (!placed)
                    {
                        var nq = new List<IGrouping<int, AtoIsland>> { atom };
                        if (TryPackQueue(nq, settings, platform, cache, report, out _))
                            queues.Add(nq);
                        else
                        {
                            report.Warn("warn.packFail", "UV group " + atom.Key + " " +
                                        string.Join(",", atom.Select(i => i.Source.name).Distinct()));
                            foreach (var i in atom) i.Eligible = false;
                        }
                    }
                }

                foreach (var q in queues)
                {
                    if (q.Count == 0) continue;
                    if (!TryPackQueue(q, settings, platform, cache, report, out var dummy) || dummy == null)
                        continue;
                    var all = q.SelectMany(x => x).ToList();
                    foreach (var roleGrp in all.GroupBy(i => i.Role))
                    {
                        var atlas = Compose(roleGrp.ToList(), dummy.Width, dummy.Height, 0, cache, settings);
                        if (atlas.Role != AtoTextureRole.Albedo)
                            MaybeDownscaleSecondary(atlas, settings, report);
                        results.Add(atlas);
                    }
                }
            }

            report.AtlasesGenerated = results.Count;
            foreach (var a in results)
            {
                report.ResultTexels += (long)a.Width * a.Height;
                report.Detail($"atlas {a.Texture.name} {a.Width}x{a.Height} util={a.Utilization:P1} role={a.Role} sources={string.Join(",", a.Sources.Select(s => s.name))}");
            }
            return results;
        }

        static bool TryPackQueue(List<IGrouping<int, AtoIsland>> queue,
            AtoPlatformOverride settings, AtoPlatform platform, AtoTextureCache cache, AtoReport report,
            out AtoAtlasResult layout)
        {
            layout = null;
            var islands = queue.SelectMany(g => g).ToList();
            if (islands.Count == 0) return true;

            // Layout masters: one island set per UV group (largest source).
            var masters = new List<AtoIsland>();
            foreach (var uv in queue)
            {
                var bySrc = uv.GroupBy(i => i.Source).OrderByDescending(g => g.Sum(AtoBitmaskPacker.OccupiedArea)).First();
                masters.AddRange(bySrc);
            }

            int area = masters.Sum(AtoBitmaskPacker.OccupiedArea);
            var pool = BuildPool(settings.experimentalNpot, AtoPlatformUtil.MaxAtlasEdge(platform));
            pool = pool.Where(c => c.w * c.h >= area).OrderBy(c => c.w * c.h)
                .ThenBy(c => Math.Max(c.w, c.h) / (float)Math.Max(1, Math.Min(c.w, c.h)))
                .ToList();

            foreach (var cand in pool)
            {
                int padPx = Mathf.Max((int)settings.minPadding, Mathf.CeilToInt(Mathf.Max(cand.w, cand.h) / 128f));
                padPx = Mathf.Max(padPx, 4);
                int padCells = Mathf.CeilToInt(padPx / (float)AtoBitmaskPacker.Granule);
                if (!TryPlace(masters, cand.w, cand.h, padCells)) continue;

                // Copy layout to every island in the same UV group with matching UV bounds.
                foreach (var uv in queue)
                {
                    var srcMasters = masters.Where(m => m.UvGroupId == uv.Key).ToList();
                    foreach (var isl in uv)
                    {
                        var m = FindTwin(isl, srcMasters);
                        if (m == null) m = srcMasters.FirstOrDefault();
                        if (m == null) continue;
                        isl.AtlasX = m.AtlasX;
                        isl.AtlasY = m.AtlasY;
                        isl.Rotated = m.Rotated;
                        isl.AtlasSizeX = m.AtlasSizeX;
                        isl.AtlasSizeY = m.AtlasSizeY;
                    }
                }

                layout = new AtoAtlasResult { Width = cand.w, Height = cand.h, Role = AtoTextureRole.Albedo };
                layout.Islands.AddRange(islands);
                return true;
            }
            return false;
        }

        static AtoIsland FindTwin(AtoIsland isl, List<AtoIsland> masters)
        {
            AtoIsland best = null;
            float bestD = float.MaxValue;
            foreach (var m in masters)
            {
                float d = Mathf.Abs(m.UvBounds.xMin - isl.UvBounds.xMin)
                          + Mathf.Abs(m.UvBounds.yMin - isl.UvBounds.yMin)
                          + Mathf.Abs(m.UvBounds.width - isl.UvBounds.width)
                          + Mathf.Abs(m.UvBounds.height - isl.UvBounds.height);
                if (d < bestD) { bestD = d; best = m; }
            }
            return bestD < 0.05f ? best : best;
        }

        struct Cand { public int w, h; }

        static List<Cand> BuildPool(bool npot, int max)
        {
            var list = new List<Cand>();
            if (!npot)
            {
                for (int e = 64; e <= max; e <<= 1)
                for (int e2 = 64; e2 <= max; e2 <<= 1)
                    list.Add(new Cand { w = e, h = e2 });
            }
            else
            {
                for (int e = 64; e <= max; e += 64)
                {
                    list.Add(new Cand { w = e, h = e });
                    if (e * 2 <= max) list.Add(new Cand { w = e * 2, h = e });
                    if (e * 2 <= max) list.Add(new Cand { w = e, h = e * 2 });
                }
            }
            return list;
        }

        static bool TryPlace(List<AtoIsland> islands, int aw, int ah, int padCells)
        {
            int cw = aw / AtoBitmaskPacker.Granule;
            int ch = ah / AtoBitmaskPacker.Granule;
            var atlas = new ulong[Math.Max(1, ((cw + 63) / 64) * Math.Max(1, ch))];
            var ordered = islands.OrderByDescending(AtoBitmaskPacker.OccupiedArea)
                .ThenByDescending(i => Mathf.Max(i.RasterW, i.RasterH)).ToList();

            foreach (var isl in ordered)
            {
                bool ok = AtoBitmaskPacker.TryBlf(atlas, cw, ch, isl.Mask, isl.RasterW, isl.RasterH, padCells,
                    out int x, out int y);
                bool rot = false;
                if (!ok)
                {
                    var tr = AtoBitmaskPacker.Transpose(isl);
                    ok = AtoBitmaskPacker.TryBlf(atlas, cw, ch, tr, isl.RasterH, isl.RasterW, padCells, out x, out y);
                    rot = ok;
                }
                if (!ok) return false;
                isl.AtlasX = x * AtoBitmaskPacker.Granule;
                isl.AtlasY = y * AtoBitmaskPacker.Granule;
                isl.Rotated = rot;
                isl.AtlasSizeX = aw;
                isl.AtlasSizeY = ah;
            }
            return true;
        }

        static AtoAtlasResult Compose(List<AtoIsland> islands, int w, int h, int pad,
            AtoTextureCache cache, AtoPlatformOverride settings)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, islands[0].Role != AtoTextureRole.Albedo)
            {
                name = "ATO_" + islands[0].Role + "_" + islands[0].Source.name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = islands.Max(i => (int)i.Source.filterMode) >= (int)FilterMode.Trilinear
                    ? FilterMode.Trilinear : FilterMode.Bilinear,
                anisoLevel = islands.Max(i => i.Source.anisoLevel)
            };
            var dest = new Color32[w * h];
            foreach (var isl in islands)
            {
                var src = cache.GetPixels(isl.Source);
                int x0 = Mathf.Clamp(Mathf.FloorToInt(isl.PixelBounds.xMin), 0, isl.Source.width - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(isl.PixelBounds.yMin), 0, isl.Source.height - 1);
                int bw = Mathf.Max(1, Mathf.RoundToInt(isl.PixelBounds.width));
                int bh = Mathf.Max(1, Mathf.RoundToInt(isl.PixelBounds.height));
                int dw = Mathf.Max(1, Mathf.RoundToInt(bw * isl.ScaleU));
                int dh = Mathf.Max(1, Mathf.RoundToInt(bh * isl.ScaleV));
                var crop = new Color32[bw * bh];
                for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                {
                    int sx = Mathf.Clamp(x0 + x, 0, isl.Source.width - 1);
                    int sy = Mathf.Clamp(y0 + y, 0, isl.Source.height - 1);
                    crop[y * bw + x] = src[sy * isl.Source.width + sx];
                }
                var scaled = AtoQualityEval.BilinearDownsample(crop, bw, bh, dw, dh, isl.Blend != AtoBlendMode.Opaque);
                if (isl.Role == AtoTextureRole.Normal)
                    Renorm(scaled);
                for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    int dx = isl.Rotated ? isl.AtlasX + y : isl.AtlasX + x;
                    int dy = isl.Rotated ? isl.AtlasY + x : isl.AtlasY + y;
                    if ((uint)dx < (uint)w && (uint)dy < (uint)h)
                        dest[dy * w + dx] = scaled[y * dw + x];
                }
                isl.Atlas = tex;
            }

            PullPush(dest, w, h, islands);
            tex.SetPixels32(dest);
            tex.Apply(false, false);

            int used = islands.Sum(AtoBitmaskPacker.OccupiedArea);
            var res = new AtoAtlasResult
            {
                Texture = tex,
                Key = islands[0].TypeKey,
                Role = islands[0].Role,
                Width = w,
                Height = h,
                Utilization = used / (float)Mathf.Max(1, w * h)
            };
            res.Islands.AddRange(islands);
            res.Sources.AddRange(islands.Select(i => i.Source).Distinct());
            return res;
        }

        static void Renorm(Color32[] px)
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

        static void MaybeDownscaleSecondary(AtoAtlasResult atlas, AtoPlatformOverride settings, AtoReport report)
        {
            if (atlas.Role == AtoTextureRole.Albedo || atlas.Islands.Count == 0) return;
            float maxS = atlas.Islands.Max(i => Mathf.Max(i.ScaleU, i.ScaleV));
            if (maxS >= 0.99f) return;
            int pad = Mathf.Max((int)settings.minPadding, 4);
            int nw = Mathf.Max(pad * 2, Mathf.ClosestPowerOfTwo(Mathf.Max(64, Mathf.RoundToInt(atlas.Width * maxS))));
            int nh = Mathf.Max(pad * 2, Mathf.ClosestPowerOfTwo(Mathf.Max(64, Mathf.RoundToInt(atlas.Height * maxS))));
            if (nw >= atlas.Width && nh >= atlas.Height) return;
            // Uniform scale keeps normalized UVs identical to the albedo atlas.
            // 均匀缩放后归一化 UV 与主色图集一致。
            var nt = new Texture2D(nw, nh, TextureFormat.RGBA32, false, true);
            var src = atlas.Texture.GetPixels32();
            var down = AtoQualityEval.BilinearDownsample(src, atlas.Width, atlas.Height, nw, nh, false);
            nt.SetPixels32(down);
            nt.Apply(false, false);
            nt.name = atlas.Texture.name;
            nt.wrapMode = TextureWrapMode.Clamp;
            nt.filterMode = atlas.Texture.filterMode;
            float sx = nw / (float)atlas.Width, sy = nh / (float)atlas.Height;
            foreach (var isl in atlas.Islands)
            {
                isl.Atlas = nt;
                isl.AtlasX = Mathf.RoundToInt(isl.AtlasX * sx);
                isl.AtlasY = Mathf.RoundToInt(isl.AtlasY * sy);
                isl.AtlasSizeX = nw;
                isl.AtlasSizeY = nh;
            }
            Object.DestroyImmediate(atlas.Texture);
            atlas.Texture = nt;
            atlas.Width = nw;
            atlas.Height = nh;
            report.Detail($"secondary atlas downscale {atlas.Texture.name} -> {nw}x{nh} (UV-normalized)");
        }

        static void PullPush(Color32[] px, int w, int h, List<AtoIsland> islands)
        {
            bool keepA0 = islands.Any(i => i.Blend != AtoBlendMode.Opaque);
            var filled = new bool[px.Length];
            for (int i = 0; i < px.Length; i++)
                filled[i] = px[i].a > 0 || (px[i].r | px[i].g | px[i].b) != 0;
            for (int iter = 0; iter < 16; iter++)
            {
                bool any = false;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (filled[i]) continue;
                    int cr = 0, cg = 0, cb = 0, n = 0;
                    for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int nx = x + ox, ny = y + oy;
                        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                        int j = ny * w + nx;
                        if (!filled[j]) continue;
                        cr += px[j].r; cg += px[j].g; cb += px[j].b; n++;
                    }
                    if (n == 0) continue;
                    px[i] = new Color32((byte)(cr / n), (byte)(cg / n), (byte)(cb / n), keepA0 ? (byte)0 : (byte)255);
                    filled[i] = true;
                    any = true;
                }
                if (!any) break;
            }
        }
    }
}
