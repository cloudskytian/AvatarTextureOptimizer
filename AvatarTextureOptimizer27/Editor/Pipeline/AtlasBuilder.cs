using System;
using System.Collections.Generic;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public sealed class AtlasResult
    {
        public TextureTypeGroup TypeGroup;
        public Texture2D Atlas;
        public AtoTextureSemantic Semantic;
        public readonly Dictionary<UvIsland, Rect> UvRects = new Dictionary<UvIsland, Rect>();
        public readonly List<Texture2D> Sources = new List<Texture2D>();
        public float Utilization;
    }

    public static class AtlasBuilder
    {
        public static List<AtlasResult> Build(List<UvGroup> groups, AtoPlatformSettings settings, AtoPlatform platform, BakeReport report)
        {
            var results = new List<AtlasResult>();
            int maxEdge = settings.ResolveMaxAtlasEdge(platform);
            var candidates = BitmaskPacker.CandidateSizes(settings.ExperimentalNpot, maxEdge);
            var typeGroups = groups.Where(g => !g.Whitelisted && g.TypeGroup != null).Select(g => g.TypeGroup).Distinct().ToList();

            foreach (var tg in typeGroups)
            {
                var queue = tg.Members.Where(g => !g.Whitelisted).OrderByDescending(RasterArea).ToList();
                var leftover = new List<UvGroup>();
                while (queue.Count > 0)
                {
                    var batch = new List<UvGroup>();
                    leftover.Clear();
                    foreach (var g in queue)
                    {
                        var tryBatch = new List<UvGroup>(batch) { g };
                        if (TryPackBatch(tryBatch, candidates, settings, out var packed, out var size))
                        {
                            batch = tryBatch;
                        }
                        else if (batch.Count == 0)
                        {
                            // single group cannot fit max atlas
                            g.Whitelisted = false;
                            foreach (var isl in g.Islands) isl.SkipAtlas = true;
                            report.Warnings.Add("UV group cannot fit max atlas: " + g.Id);
                            AtoLog.Warn("Single UV group cannot fit max atlas, skip atlas: " + g.Id);
                        }
                        else leftover.Add(g);
                    }
                    if (batch.Count > 0 && TryPackBatch(batch, candidates, settings, out var places, out var atlasSize))
                    {
                        results.AddRange(BlitAtlases(tg, batch, places, atlasSize, settings, report));
                    }
                    queue = leftover;
                }
            }

            report.AtlasCount = results.Count;
            if (results.Count > 0)
                report.Utilization = results.Average(r => r.Utilization);
            AtoLog.Info($"Atlases built={results.Count} util={report.Utilization:P1}");
            return results;
        }

        static long RasterArea(UvGroup g)
        {
            long a = 0;
            foreach (var i in g.Islands)
                a += (long)Mathf.CeilToInt(i.PixelBounds.width * i.ScaleU) *
                     Mathf.CeilToInt(i.PixelBounds.height * i.ScaleV);
            return a;
        }

        static bool TryPackBatch(List<UvGroup> batch, List<Vector2Int> candidates, AtoPlatformSettings settings,
            out List<BitmaskPacker.Placement> places, out Vector2Int size)
        {
            places = null;
            size = default;
            long need = 0;
            var masks = new List<BitmaskPacker.IslandMask>();
            int id = 0;
            var idMap = new Dictionary<int, UvIsland>();
            foreach (var g in batch)
            foreach (var isl in g.Islands)
            {
                int w = Mathf.Max(1, Mathf.CeilToInt(isl.PixelBounds.width * isl.ScaleU));
                int h = Mathf.Max(1, Mathf.CeilToInt(isl.PixelBounds.height * isl.ScaleV));
                var dummy = new List<Vector2>();
                for (int y = 0; y < h; y += BitmaskPacker.Granularity)
                for (int x = 0; x < w; x += BitmaskPacker.Granularity)
                    dummy.Add(new Vector2(x, y));
                var m = BitmaskPacker.Rasterize(dummy, w, h);
                m.Id = id;
                idMap[id] = isl;
                masks.Add(m);
                need += w * h;
                id++;
            }

            foreach (var c in candidates)
            {
                if ((long)c.x * c.y < need) continue;
                int padPx = Mathf.Max((int)settings.MinPadding, Mathf.CeilToInt(Mathf.Max(c.x, c.y) / 128f));
                padPx = Mathf.Max(4, padPx);
                int padCells = Mathf.CeilToInt(padPx / (float)BitmaskPacker.Granularity);
                var outP = new List<BitmaskPacker.Placement>();
                var copy = new List<BitmaskPacker.IslandMask>(masks);
                if (!BitmaskPacker.TryPack(copy, c.x, c.y, padCells, outP)) continue;
                places = outP;
                size = c;
                return true;
            }
            return false;
        }

        static List<AtlasResult> BlitAtlases(TextureTypeGroup tg, List<UvGroup> batch,
            List<BitmaskPacker.Placement> places, Vector2Int size, AtoPlatformSettings settings, BakeReport report)
        {
            var list = new List<AtlasResult>();
            var semantics = batch.SelectMany(g => g.Semantics).Distinct().ToList();
            if (semantics.Count == 0) semantics.Add(AtoTextureSemantic.Albedo);

            // map placement by sequential id
            int idx = 0;
            var placeOf = new Dictionary<UvIsland, BitmaskPacker.Placement>();
            foreach (var g in batch)
            foreach (var isl in g.Islands)
            {
                if (idx < places.Count) placeOf[isl] = places[idx];
                idx++;
            }

            foreach (var sem in semantics)
            {
                var tex = new Texture2D(size.x, size.y, TextureFormat.RGBA32, true, sem != AtoTextureSemantic.Albedo);
                tex.name = "ATO_" + tg.Id + "_" + sem;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.Apply(false, false);
                var fill = new Color[size.x * size.y];
                tex.SetPixels(fill);

                var result = new AtlasResult { TypeGroup = tg, Atlas = tex, Semantic = sem };
                long used = 0;
                foreach (var g in batch)
                {
                    Texture2D src = PickSource(g, sem);
                    if (src != null) result.Sources.Add(src);
                    foreach (var isl in g.Islands)
                    {
                        if (!placeOf.TryGetValue(isl, out var p)) continue;
                        BlitIsland(src, isl, tex, p);
                        used += p.W * p.H;
                        result.UvRects[isl] = new Rect(p.X / (float)size.x, p.Y / (float)size.y, p.W / (float)size.x, p.H / (float)size.y);
                    }
                }
                PullPush.Fill(tex, sem == AtoTextureSemantic.Albedo);
                tex.Apply(true, false);
                SaveTempAsset(tex);
                result.Utilization = used / (float)(size.x * size.y);
                report.AtlasPixels += (long)size.x * size.y;
                report.Details.Add($"Atlas {tex.name} {size.x}x{size.y} util={result.Utilization:P1} sources={string.Join(",", result.Sources.Select(s => s.name))}");
                AtoLog.Info($"Atlas {tex.name} {size.x}x{size.y} util={result.Utilization:P1}");
                list.Add(result);
                tg.Atlases.Add(tex);
            }
            return list;
        }

        static Texture2D PickSource(UvGroup g, AtoTextureSemantic sem)
        {
            for (int i = 0; i < g.Textures.Count; i++)
                if (i < g.Semantics.Count && g.Semantics[i] == sem) return g.Textures[i];
            return g.Textures.Count > 0 ? g.Textures[0] : null;
        }

        static void BlitIsland(Texture2D src, UvIsland isl, Texture2D dst, BitmaskPacker.Placement p)
        {
            if (src == null || !src.isReadable) return;
            try
            {
                var r = isl.PixelBounds;
                r.x = Mathf.Clamp(r.x, 0, src.width - 1);
                r.y = Mathf.Clamp(r.y, 0, src.height - 1);
                r.width = Mathf.Clamp(r.width, 1, src.width - r.x);
                r.height = Mathf.Clamp(r.height, 1, src.height - r.y);
                var px = src.GetPixels(r.x, r.y, r.width, r.height);
                var small = QualityMetrics.PremultipliedDownsample(px, r.width, r.height, Mathf.Max(1, p.W), Mathf.Max(1, p.H));
                dst.SetPixels(p.X, p.Y, Mathf.Max(1, p.W), Mathf.Max(1, p.H), small);
            }
            catch (Exception e)
            {
                AtoLog.Warn("Blit island failed: " + e.Message);
            }
        }

        static void SaveTempAsset(Texture2D tex)
        {
            const string dir = "Assets/ATO_Generated";
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder("Assets", "ATO_Generated");
            string path = dir + "/" + tex.name + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".asset";
            AssetDatabase.CreateAsset(tex, path);
        }
    }
}
