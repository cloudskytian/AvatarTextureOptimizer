// BLFPacker.cs / BLFPacker.cs
// Bottom-Left Fill bin packing on a 4-pixel bitmask. Uses triangle-rasterized island masks
// (not axis-aligned bounding boxes) for accurate packing density.
// 在4像素位掩码上的Bottom-Left Fill装箱。使用三角形光栅化的岛mask（非轴对齐包围盒）以获得准确装箱密度。

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Atlas
{
    public class PackItem
    {
        public Groups.UVGroup Group;
        public Vector2Int TargetPixelSize;
        public int GridW, GridH;
        public ulong[] Mask;        // item mask (without padding)
        public ulong[] PaddedMask;  // mask with padding dilation
        public ulong[] MaskRotated;
        public ulong[] PaddedRotated;
        public int RotatedGridW, RotatedGridH;
        public bool AllowRotation = true;
    }

    public readonly struct CandidateAtlas
    {
        public readonly int W, H;
        public CandidateAtlas(int w, int h) { W = w; H = h; }
        public long Area => (long)W * H;
        public float Aspect => (float)Math.Max(W, H) / Mathf.Max(1, Math.Min(W, H));
    }

    public static class BLFPacker
    {
        private const int MinSize = 64;

        public static List<CandidateAtlas> GenerateCandidatePool(int maxSize, bool allowNPOT)
        {
            var pool = new List<CandidateAtlas>();
            if (allowNPOT)
            {
                for (int w = MinSize; w <= maxSize; w += 64)
                    for (int h = MinSize; h <= maxSize; h += 64)
                        pool.Add(new CandidateAtlas(w, h));
            }
            else
            {
                for (int w = MinSize; w <= maxSize; w *= 2)
                    for (int h = MinSize; h <= maxSize; h *= 2)
                        pool.Add(new CandidateAtlas(w, h));
            }
            pool.Sort((a, b) =>
            {
                int c = a.Area.CompareTo(b.Area);
                if (c != 0) return c;
                return a.Aspect.CompareTo(b.Aspect);
            });
            return pool;
        }

        public static List<AtlasTexture> Pack(List<PackItem> items, List<CandidateAtlas> pool, int padding, int maxSize,
            bool isNormal, bool hasAlpha, string namePrefix, out List<PackItem> skipped)
        {
            skipped = new List<PackItem>();
            var result = new List<AtlasTexture>();

            // Sort by area desc, then max edge desc
            items.Sort((a, b) =>
            {
                long aa = (long)a.GridW * a.GridH;
                long bb = (long)b.GridW * b.GridH;
                int c = bb.CompareTo(aa);
                return c != 0 ? c : Math.Max(b.GridW, b.GridH).CompareTo(Math.Max(a.GridW, a.GridH));
            });

            // Pre-dilate masks with padding / 预先用padding外扩mask
            foreach (var it in items)
            {
                int pad = Mathf.Max(1, (padding + Rasterization.GRAN - 1) / Rasterization.GRAN);
                it.PaddedMask = Rasterization.DilateMask(it.Mask, it.GridW, it.GridH, pad);
                try
                {
                    it.MaskRotated = Rasterization.Transpose(it.Mask, it.GridW, it.GridH, out int rw, out int rh);
                    it.RotatedGridW = rw; it.RotatedGridH = rh;
                    it.PaddedRotated = Rasterization.DilateMask(it.MaskRotated, it.RotatedGridW, it.RotatedGridH, pad);
                }
                catch { it.MaskRotated = null; }
                if (!it.AllowRotation) { it.MaskRotated = null; it.PaddedRotated = null; }
            }

            int idx = 0;
            int atlasIdx = 0;
            while (idx < items.Count)
            {
                long remainingCells = 0;
                for (int i = idx; i < items.Count; i++) remainingCells += Rasterization.PopCount(items[i].PaddedMask);
                long areaThreshold = remainingCells * Rasterization.GRAN * Rasterization.GRAN;

                var atlas = TryPackAtlas(items, ref idx, pool, areaThreshold, maxSize, isNormal, hasAlpha,
                    namePrefix + "_" + atlasIdx, out var thisSkipped);
                if (atlas == null)
                {
                    // Couldn't even fit one item -> skip remaining
                    for (int i = idx; i < items.Count; i++) skipped.Add(items[i]);
                    break;
                }
                skipped.AddRange(thisSkipped);
                result.Add(atlas);
                atlasIdx++;
            }
            return result;
        }

        private static AtlasTexture TryPackAtlas(List<PackItem> items, ref int startIdx, List<CandidateAtlas> pool,
            long areaThreshold, int maxSize, bool isNormal, bool hasAlpha, string name, out List<PackItem> skipped)
        {
            skipped = new List<PackItem>();
            var candidate = pool.FirstOrDefault(c => c.Area >= areaThreshold && c.W <= maxSize && c.H <= maxSize);
            if (candidate.W == 0) candidate = pool.Last();
            var sizes = pool.Where(c => c.Area >= areaThreshold && c.W <= maxSize && c.H <= maxSize).ToList();
            if (sizes.Count == 0) sizes = new List<CandidateAtlas> { pool.Last() };

            foreach (var cand in sizes)
            {
                var res = TryPackOnSize(items, startIdx, cand.W, cand.H, isNormal, hasAlpha, name);
                if (res != null)
                {
                    startIdx = res.nextIdx;
                    skipped = res.skipped;
                    return res.atlas;
                }
            }
            // Fallback to max size / 回退到最大尺寸
            var max = pool.Last();
            var r = TryPackOnSize(items, startIdx, max.W, max.H, isNormal, hasAlpha, name);
            if (r != null) { startIdx = r.nextIdx; skipped = r.skipped; return r.atlas; }
            return null;
        }

        private static (AtlasTexture atlas, int nextIdx, List<PackItem> skipped)? TryPackOnSize(
            List<PackItem> items, int startIdx, int w, int h, bool isNormal, bool hasAlpha, string name)
        {
            int gridW = (w + Rasterization.GRAN - 1) / Rasterization.GRAN;
            int gridH = (h + Rasterization.GRAN - 1) / Rasterization.GRAN;
            int wpr = (gridW + 63) / 64;
            var atlasMask = new ulong[gridH * wpr];
            var atlas = new AtlasTexture
            {
                Name = name, Width = w, Height = h,
                UsageFlags = (isNormal ? Core.TextureUsageFlags.Normal : Core.TextureUsageFlags.BaseColor)
                            | (hasAlpha ? Core.TextureUsageFlags.HasAlpha : 0)
            };
            int nextIdx = startIdx;
            var skipped = new List<PackItem>();
            bool placedAny = false;

            for (int i = startIdx; i < items.Count; i++)
            {
                var it = items[i];
                bool placed = false;

                // Try non-rotated / 尝试非旋转
                for (int gy = 0; gy + it.GridH <= gridH && !placed; gy++)
                    for (int gx = 0; gx + it.GridW <= gridW && !placed; gx++)
                    {
                        if (Rasterization.TryPlace(atlasMask, gridW, gridH, it.PaddedMask ?? it.Mask, it.GridW, it.GridH, gx, gy))
                        {
                            float px = gx * Rasterization.GRAN, py = gy * Rasterization.GRAN;
                            float pw = it.GridW * Rasterization.GRAN, ph = it.GridH * Rasterization.GRAN;
                            // Clamp to atlas size / 钳制到图集尺寸
                            pw = Mathf.Min(pw, w - px); ph = Mathf.Min(ph, h - py);
                            atlas.Placements.Add((it.Group, new Rect(px, py, pw, ph), false));
                            placed = placedAny = true;
                            nextIdx = i + 1;
                        }
                    }
                // Try rotated / 尝试旋转
                if (!placed && it.AllowRotation && it.PaddedRotated != null)
                {
                    for (int gy = 0; gy + it.RotatedGridH <= gridH && !placed; gy++)
                        for (int gx = 0; gx + it.RotatedGridW <= gridW && !placed; gx++)
                        {
                            if (Rasterization.TryPlace(atlasMask, gridW, gridH, it.PaddedRotated, it.RotatedGridW, it.RotatedGridH, gx, gy))
                            {
                                float px = gx * Rasterization.GRAN, py = gy * Rasterization.GRAN;
                                float pw = it.RotatedGridW * Rasterization.GRAN, ph = it.RotatedGridH * Rasterization.GRAN;
                                pw = Mathf.Min(pw, w - px); ph = Mathf.Min(ph, h - py);
                                atlas.Placements.Add((it.Group, new Rect(px, py, pw, ph), true));
                                placed = placedAny = true;
                                nextIdx = i + 1;
                            }
                        }
                }
                if (!placed)
                {
                    // Spill into next atlas; leave it for next iteration
                    // 溢出到下一个图集；留到下一轮
                    // (Per spec: if single item doesn't fit max atlas, skip it entirely)
                    skipped.Add(it);
                }
            }

            if (!placedAny) return null;
            int occ = Rasterization.PopCount(atlasMask);
            atlas.Utilization = (float)occ / (gridW * gridH);
            return (atlas, nextIdx, skipped);
        }
    }
}
