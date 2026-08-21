using System;
using System.Collections.Generic;

// Pure C# packing core. NO Unity dependencies — compiles in Unity (netstandard) and in the dotnet test harness.
// 纯 C# 装箱核心。不依赖 Unity —— 可在 Unity 与 dotnet 单测中编译。
//
// Model: a UVGroup is the atomic pack unit. Islands of one group are laid out ONCE at reference
// resolution (D_max) producing normalized rects (position/size/rotation). Every atlas (bucket) that
// contains textures of this group then instantiates those rects at its own resolution, so the same
// UV maps to the same normalized position in every atlas.
//
// 模型：UV 组是装箱原子单位。组内岛先在参考分辨率 D_max 上做一次布局得到归一化矩形（位置/尺寸/旋转），
// 包含该组贴图的每个图集（桶）再按各自分辨率实例化这些矩形，从而保证同一 UV 在所有图集位置一致。

namespace Net.Fosa.AvatarTextureOptimizer.Pure
{
    public struct AtoVec2
    {
        public float x, y;
        public AtoVec2(float x, float y) { this.x = x; this.y = y; }
    }

    /// <summary>Integer rect (pixels). 整数矩形（像素）。</summary>
    public struct AtoRectI
    {
        public int x, y, w, h;
        public AtoRectI(int x, int y, int w, int h) { this.x = x; this.y = y; this.w = w; this.h = h; }
        public int Area => w * h;
        public override string ToString() => $"({x},{y} {w}x{h})";
    }

    /// <summary>Float rect (normalized UV space). 浮点矩形（归一化 UV 空间）。</summary>
    public struct AtoRectF
    {
        public float x, y, w, h;
        public AtoRectF(float x, float y, float w, float h) { this.x = x; this.y = y; this.w = w; this.h = h; }
    }

    /// <summary>
    /// Binary mask over a grid of 4px blocks (the packing granularity).
    /// 4px 块网格上的二值掩码（装箱粒度）。
    /// </summary>
    public sealed class BitMask
    {
        public readonly int WidthBlocks;   // in blocks. 块宽。
        public readonly int HeightBlocks;  // in blocks. 块高。
        private readonly ulong[] _bits;

        public BitMask(int widthBlocks, int heightBlocks)
        {
            WidthBlocks = Math.Max(1, widthBlocks);
            HeightBlocks = Math.Max(1, heightBlocks);
            _bits = new ulong[(HeightBlocks * ((WidthBlocks + 63) >> 6))];
        }

        public bool Get(int bx, int by)
        {
            if (bx < 0 || by < 0 || bx >= WidthBlocks || by >= HeightBlocks) return false;
            int word = by * ((WidthBlocks + 63) >> 6) + (bx >> 6);
            return (_bits[word] & (1UL << (bx & 63))) != 0;
        }

        public void Set(int bx, int by, bool v)
        {
            if (bx < 0 || by < 0 || bx >= WidthBlocks || by >= HeightBlocks) return;
            int word = by * ((WidthBlocks + 63) >> 6) + (bx >> 6);
            if (v) _bits[word] |= 1UL << (bx & 63);
            else _bits[word] &= ~(1UL << (bx & 63));
        }

        /// <summary>True if any block set. 是否存在已置位块。</summary>
        public bool AnySet
        {
            get { foreach (var v in _bits) if (v != 0) return true; return false; }
        }

        /// <summary>Number of set blocks. 置位块数量。</summary>
        public int PopCount()
        {
            int n = 0;
            foreach (var v in _bits) n += BitCount(v);
            return n;
        }
        private static int BitCount(ulong v) { int c = 0; while (v != 0) { v &= v - 1; c++; } return c; }

        /// <summary>Bounding box of set blocks, in blocks. 置位块的包围盒（块单位）。</summary>
        public AtoRectI Bounds()
        {
            int minX = WidthBlocks, minY = HeightBlocks, maxX = -1, maxY = -1;
            for (int y = 0; y < HeightBlocks; y++)
            {
                int wordsPerRow = (WidthBlocks + 63) >> 6;
                int rowStart = y * wordsPerRow;
                for (int x = 0; x < WidthBlocks; x++)
                {
                    if (Get(x, y))
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0) return new AtoRectI(0, 0, 0, 0);
            return new AtoRectI(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        /// <summary>90° clockwise rotation via transpose+flip (bit-level). 90° 顺时针旋转（转置+翻转）。</summary>
        public BitMask Rotate90()
        {
            var r = new BitMask(HeightBlocks, WidthBlocks);
            for (int y = 0; y < HeightBlocks; y++)
                for (int x = 0; x < WidthBlocks; x++)
                    if (Get(x, y)) r.Set(HeightBlocks - 1 - y, x, true);
            return r;
        }

        /// <summary>True if this mask, placed at (px,py) blocks, overlaps `other` placed at (ox,oy). 判断与其他掩码是否重叠。</summary>
        public bool Intersects(BitMask other, int px, int py, int ox, int oy)
        {
            int x0 = Math.Max(px, ox), x1 = Math.Min(px + WidthBlocks, ox + other.WidthBlocks);
            int y0 = Math.Max(py, oy), y1 = Math.Min(py + HeightBlocks, oy + other.HeightBlocks);
            if (x0 >= x1 || y0 >= y1) return false;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    if (Get(x - px, y - py) && other.Get(x - ox, y - oy)) return true;
            return false;
        }
    }

    /// <summary>
    /// Triangle rasterization to a 4px-block bit mask (conservative center-sample fill).
    /// 三角形光栅化到 4px 块位掩码（块中心采样填充）。
    /// </summary>
    public static class AtoRaster
    {
        /// <summary>
        /// Rasterizes the given triangles into a bit mask.
        /// uv: 2 floats per vertex (absolute UV). tris: 3 indices per triangle.
        /// The island's pixel region [minU..maxU]x[minV..maxV] maps to pixelW x pixelH pixels;
        /// the returned mask covers that region at 4px blocks.
        /// uv：每顶点 2 个浮点（绝对 UV）。tris：每三角形 3 个索引。
        /// 岛的像素区域 [minU..maxU]x[minV..maxV] 映射到 pixelW×pixelH 像素；返回掩码覆盖该区域（4px 块）。
        /// </summary>
        public static BitMask RasterizeTriangles(float[] uv, int[] tris, float minU, float minV, float maxU, float maxV, int pixelW, int pixelH)
        {
            int blockW = Math.Max(1, (pixelW + 3) >> 2);
            int blockH = Math.Max(1, (pixelH + 3) >> 2);
            var mask = new BitMask(blockW, blockH);

            float spanU = maxU - minU, spanV = maxV - minV;
            if (spanU <= 0f || spanV <= 0f) return mask;
            float invU = 1f / spanU, invV = 1f / spanV;

            int triCount = tris.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                // Vertex positions in block coordinates (block centers at 2px offsets). 顶点块坐标（块中心偏移 2px）。
                float bx0 = (uv[i0 * 2] - minU) * invU * blockW, by0 = (uv[i0 * 2 + 1] - minV) * invV * blockH;
                float bx1 = (uv[i1 * 2] - minU) * invU * blockW, by1 = (uv[i1 * 2 + 1] - minV) * invV * blockH;
                float bx2 = (uv[i2 * 2] - minU) * invU * blockW, by2 = (uv[i2 * 2 + 1] - minV) * invV * blockH;

                int minBX = ClampBlock((int)Math.Floor(Math.Min(bx0, Math.Min(bx1, bx2))), blockW);
                int maxBX = ClampBlock((int)Math.Ceiling(Math.Max(bx0, Math.Max(bx1, bx2))), blockW);
                int minBY = ClampBlock((int)Math.Floor(Math.Min(by0, Math.Min(by1, by2))), blockH);
                int maxBY = ClampBlock((int)Math.Ceiling(Math.Max(by0, Math.Max(by1, by2))), blockH);

                for (int by = minBY; by < maxBY; by++)
                {
                    for (int bx = minBX; bx < maxBX; bx++)
                    {
                        if (mask.Get(bx, by)) continue;
                        // Block center in block coords (center of the 4px block). 块中心（4px 块中心）。
                        float cx = bx + 0.5f, cy = by + 0.5f;
                        if (PointInTriangle(cx, cy, bx0, by0, bx1, by1, bx2, by2)) mask.Set(bx, by, true);
                    }
                }
            }
            return mask;
        }

        private static int ClampBlock(int v, int n) => v < 0 ? 0 : (v > n ? n : v);

        public static bool PointInTriangle(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
        {
            float d1 = Cross(px, py, ax, ay, bx, by);
            float d2 = Cross(px, py, bx, by, cx, cy);
            float d3 = Cross(px, py, cx, cy, ax, ay);
            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }
        private static float Cross(float px, float py, float ax, float ay, float bx, float by)
            => (bx - ax) * (py - ay) - (by - ay) * (px - ax);
    }

    /// <summary>
    /// One item to pack: its shape mask (already at its final pixel size, rounded to 4px).
    /// 待装箱项：形状掩码（已是最终像素尺寸，4px 对齐）。
    /// </summary>
    public sealed class PackItem
    {
        public BitMask Mask;
        public object Tag;
        public int WidthPx => Mask.WidthBlocks * 4;
        public int HeightPx => Mask.HeightBlocks * 4;
        public int BlockArea => Mask.PopCount();
    }

    public struct Placement
    {
        public int X, Y;        // pixels. 像素。
        public bool Rotated;    // 90° rotated. 是否 90° 旋转。
        public object Tag;
        public int W, H;        // final pixel size (after rotation). 旋转后的最终像素尺寸。
    }

    /// <summary>
    /// Bottom-Left-Fill packing with full-area scan. Items sorted by (raster area desc, long edge desc).
    /// For each candidate y the scan skips directly to the next valid x using the first occupied block
    /// of every item row (boundary-skip), so the search is both exhaustive and fast.
    /// 全扫描 BLF 装箱。按（光栅面积降序、长边降序）排序；对每个候选 y，利用各物品行的首个占用块
    /// 直接跳到下一个可能合法的 x（边界跳过），既穷尽又高效。
    /// </summary>
    public static class AtoBLF
    {
        public static bool TryPack(List<PackItem> items, int atlasW, int atlasH, int padPx, List<Placement> result)
        {
            int block = 4;
            int aw = atlasW / block, ah = atlasH / block;
            if (aw < 1 || ah < 1) return false;
            int padB = Math.Max(0, (padPx + block - 1) / block);

            var sorted = new List<PackItem>(items);
            sorted.Sort((a, b) =>
            {
                int byArea = b.BlockArea.CompareTo(a.BlockArea);
                if (byArea != 0) return byArea;
                int la = Math.Max(a.WidthPx, a.HeightPx), lb = Math.Max(b.WidthPx, b.HeightPx);
                int byEdge = lb.CompareTo(la);
                if (byEdge != 0) return byEdge;
                return (a.WidthPx * a.HeightPx).CompareTo(b.WidthPx * b.HeightPx);
            });

            // Occupancy grid (blocks). 占用网格（块）。
            var occ = new byte[aw * ah];

            bool RowRangeClear(int y, int x0, int x1) // x1 exclusive. 行区间是否全空。
            {
                if (x0 < 0 || x1 > aw) return false;
                int rowStart = y * aw;
                for (int x = x0; x < x1; x++) if (occ[rowStart + x] != 0) return false;
                return true;
            }

            // First occupied column >= x0 in the given row; returns aw if none. 行内第一个 >= x0 的占用列；无则返回 aw。
            int FirstBlocked(int y, int x0, int limit)
            {
                int rowStart = y * aw;
                for (int x = x0; x < limit; x++) if (occ[rowStart + x] != 0) return x;
                return limit;
            }

            for (int i = 0; i < sorted.Count; i++)
            {
                var item = sorted[i];
                bool placed = false;

                // Try 0° then 90°. 先 0° 再 90°。
                for (int rot = 0; rot < 2 && !placed; rot++)
                {
                    var m = rot == 0 ? item.Mask : item.Mask.Rotate90();
                    int iw = m.WidthBlocks, ih = m.HeightBlocks;
                    int iwP = iw * block, ihP = ih * block;
                    if (iwP > atlasW || ihP > atlasH) continue;

                    int searchW = aw - iw;
                    int searchH = ah - ih;
                    if (searchW < 0 || searchH < 0) continue;

                    // The mask sits at (x,y); its halo of padB blocks must be clear of committed mask blocks,
                    // guaranteeing content-to-content gap >= padPx (island spacing = padding).
                    // 掩码位于 (x,y)；其 padB 块光环内不能有已提交的掩码块，保证内容间距 >= padPx（岛间距 = padding）。
                    bool TryAt(int x, int y)
                    {
                        // Only the in-bounds part of the halo is checked. 只检查光环在图集内的部分。
                        int cx0 = Math.Max(0, x - padB);
                        int cx1 = Math.Min(aw, x + iw + padB);
                        for (int r = y - padB; r < y + ih + padB; r++)
                        {
                            if (r < 0 || r >= ah) continue;
                            if (!RowRangeClear(r, cx0, cx1)) return false;
                        }
                        return true;
                    }

                    for (int y = 0; y <= searchH && !placed; y++)
                    {
                        int x = 0;
                        while (x <= searchW && !placed)
                        {
                            if (TryAt(x, y))
                            {
                                // Commit only the mask blocks (the halo stays free but spaced).
                                // 只提交掩码块（光环保持空闲但被间距隔开）。
                                for (int r = 0; r < ih; r++)
                                    for (int c = 0; c < iw; c++)
                                        if (m.Get(c, r)) occ[(y + r) * aw + (x + c)] = 1;
                                result.Add(new Placement { X = x * block, Y = y * block, Rotated = rot == 1, Tag = item.Tag, W = iwP, H = ihP });
                                placed = true;
                                break;
                            }

                            // Skip to just past the rightmost relevant blocked column among the item's halo rows:
                            // every halo row must be clear, so x must exceed every blocked column plus its halo.
                            // 跳到物品光环各行中相关的最右侧占用块之后：所有光环行都须为空，故 x 需超过每个占用块加其光环。
                            int skip = -1;
                            for (int r = y - padB; r < y + ih + padB; r++)
                            {
                                if (r < 0 || r >= ah) continue;
                                int b = FirstBlocked(r, Math.Max(0, x - padB), Math.Min(x + iw + padB, aw));
                                if (b < Math.Min(x + iw + padB, aw))
                                {
                                    int cand = b + padB + 1;
                                    if (cand > skip) skip = cand;
                                }
                            }
                            if (skip > x) x = skip;
                            else x++;
                        }
                    }
                }

                if (!placed) return false; // Cannot fit this item. 该物品放不下。
            }
            return true;
        }
    }

    /// <summary>
    /// Rect BLF used for placing whole UVGroup macro-rects inside one atlas.
    /// 用于将整个 UV 组宏观矩形放入图集的矩形 BLF。
    /// </summary>
    public static class AtoRectBLF
    {
        public static bool TryPack(List<AtoRectI> rects, int atlasW, int atlasH, int padPx, List<AtoRectI> result)
        {
            var sorted = new List<AtoRectI>(rects);
            sorted.Sort((a, b) =>
            {
                int d = b.Area.CompareTo(a.Area);
                if (d != 0) return d;
                return Math.Max(b.w, b.h).CompareTo(Math.Max(a.w, a.h));
            });

            var occ = new bool[atlasW * atlasH];
            for (int i = 0; i < sorted.Count; i++)
            {
                var r = sorted[i];
                int w = r.w + padPx, h = r.h + padPx;
                bool placed = false;
                for (int y = 0; y + h <= atlasH && !placed; y++)
                {
                    for (int x = 0; x + w <= atlasW; x++)
                    {
                        bool ok = true;
                        for (int ry = 0; ry < h && ok; ry++)
                            for (int rx = 0; rx < w; rx++)
                                if (occ[(y + ry) * atlasW + (x + rx)]) { ok = false; break; }
                        if (ok)
                        {
                            for (int ry = 0; ry < h; ry++)
                                for (int rx = 0; rx < w; rx++) occ[(y + ry) * atlasW + (x + rx)] = true;
                            result.Add(new AtoRectI(x, y, r.w, r.h));
                            placed = true;
                            break;
                        }
                    }
                }
                if (!placed) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Candidate atlas edge-length pool.
    /// 候选图集边长池。
    /// </summary>
    public static class AtoAtlasSizes
    {
        public static List<int> Candidates(int maxDim, bool powerOfTwo)
        {
            var list = new List<int>();
            if (powerOfTwo)
            {
                for (int s = 64; s <= maxDim; s *= 2) list.Add(s);
            }
            else
            {
                for (int s = 64; s <= maxDim; s += 64) list.Add(s);
            }
            return list;
        }

        /// <summary>
        /// Smallest candidate >= needed. Returns -1 if beyond max.
        /// 不小于 needed 的最小候选；超过上限返回 -1。
        /// </summary>
        public static int SmallestAtLeast(List<int> candidates, int needed)
        {
            foreach (var c in candidates) if (c >= needed) return c;
            return -1;
        }
    }

    /// <summary>
    /// Result of laying out one UVGroup's islands at reference resolution.
    /// 一个 UV 组的岛在参考分辨率下的布局结果。
    /// </summary>
    public sealed class GroupLayout
    {
        /// <summary>island tag -> normalized rect (position & size relative to [0,1]). tag → 归一化矩形。</summary>
        public Dictionary<object, AtoRectF> IslandRects = new Dictionary<object, AtoRectF>();
        /// <summary>island tag -> rotation 0 or 1 (90°). tag → 旋转。</summary>
        public Dictionary<object, bool> Rotations = new Dictionary<object, bool>();
        /// <summary>Group macro size (normalized). 组宏观尺寸（归一化）。</summary>
        public AtoRectF BoundsUV;
        /// <summary>Reference resolution used. 使用的参考分辨率。</summary>
        public int ReferenceDim;
        /// <summary>Whether layout succeeded at all. 是否布局成功。</summary>
        public bool Success;
    }

    /// <summary>
    /// Lays out one UVGroup: packs its island masks at ReferenceDim x ReferenceDim, yields normalized rects.
    /// 布局单个 UV 组：在 ReferenceDim×ReferenceDim 上装箱岛掩码，产出归一化矩形。
    /// </summary>
    public static class AtoGroupLayout
    {
        public static GroupLayout Layout(List<PackItem> islands, int referenceDim, int padPx)
        {
            var result = new GroupLayout { ReferenceDim = referenceDim };
            var placements = new List<Placement>();
            if (!AtoBLF.TryPack(islands, referenceDim, referenceDim, padPx, placements))
            {
                result.Success = false;
                return result;
            }
            result.Success = true;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            foreach (var p in placements)
            {
                int x1 = p.X + p.W, y1 = p.Y + p.H;
                if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                if (x1 > maxX) maxX = x1; if (y1 > maxY) maxY = y1;
                result.IslandRects[p.Tag] = new AtoRectF((float)p.X / referenceDim, (float)p.Y / referenceDim, (float)p.W / referenceDim, (float)p.H / referenceDim);
                result.Rotations[p.Tag] = p.Rotated;
            }
            result.BoundsUV = new AtoRectF((float)minX / referenceDim, (float)minY / referenceDim, (float)(maxX - minX) / referenceDim, (float)(maxY - minY) / referenceDim);
            return result;
        }
    }

    /// <summary>
    /// Assembles per-bucket atlases from group layouts.
    /// groupTag -> group layout (already computed). Returns: for each generated atlas,
    /// the list of (groupTag, groupOriginUV) plus the chosen dimension; islands then use
    /// origin + normalized rect.
    /// 从组布局装配每桶图集：返回每个图集的组标签、组原点 UV 与所选边长。
    /// </summary>
    public sealed class AtlasAssembly
    {
        public int Dimension;
        public List<object> GroupTags = new List<object>();
        public List<AtoRectF> GroupOriginsUV = new List<AtoRectF>();
    }

    public static class AtoAtlasAssembly
    {
        /// <summary>
        /// Splits the given groups into one or more atlases (count is unbounded, growing naturally).
        /// For each atlas it picks the smallest candidate dimension that fits at least one group, then
        /// greedily fills it with as many remaining groups as fit (largest first). Groups that cannot fit
        /// even the largest atlas are omitted and must be handled as fallback by the caller.
        /// 将给定组自然分裂为一张或多张图集（数量不限）。每张图集取能装下至少一组的最小候选边长，
        /// 然后按面积降序贪婪填充尽量多的剩余组。即使最大图集也装不下的组被省略，由调用方回退处理。
        /// </summary>
        public static List<AtlasAssembly> Assemble(
            List<KeyValuePair<object, GroupLayout>> groups,
            List<int> candidateDims,
            int padPx)
        {
            var result = new List<AtlasAssembly>();
            var remaining = new List<KeyValuePair<object, GroupLayout>>(groups);
            // Area-desc order so the largest groups are packed first. 面积降序，先装大组。
            remaining.Sort((a, b) =>
            {
                float aa = a.Value.BoundsUV.w * a.Value.BoundsUV.h, ab = b.Value.BoundsUV.w * b.Value.BoundsUV.h;
                return ab.CompareTo(aa);
            });

            while (remaining.Count > 0)
            {
                AtlasAssembly assembly = null;
                int chosenDim = 0;
                foreach (var dim in candidateDims)
                {
                    // Greedy fill at this dimension: keep adding groups while they fit.
                    // 在该边长下贪婪填充：能装下就继续加入组。
                    var macros = new List<AtoRectI>();
                    var taken = new List<object>();
                    var placements = new List<AtoRectI>();
                    foreach (var kv in remaining)
                    {
                        var g = kv.Value;
                        int gw = Math.Max(1, (int)Math.Ceiling(g.BoundsUV.w * dim));
                        int gh = Math.Max(1, (int)Math.Ceiling(g.BoundsUV.h * dim));
                        var trial = new List<AtoRectI>(macros) { new AtoRectI(0, 0, gw, gh) };
                        var trialP = new List<AtoRectI>();
                        if (AtoRectBLF.TryPack(trial, dim, dim, padPx, trialP))
                        {
                            macros = trial;
                            placements = trialP;
                            taken.Add(kv.Key);
                        }
                    }
                    if (taken.Count > 0)
                    {
                        assembly = new AtlasAssembly { Dimension = dim };
                        for (int i = 0; i < taken.Count; i++)
                        {
                            assembly.GroupTags.Add(taken[i]);
                            assembly.GroupOriginsUV.Add(new AtoRectF((float)placements[i].x / dim, (float)placements[i].y / dim, 0, 0));
                        }
                        chosenDim = dim;
                        break;
                    }
                }

                if (assembly == null)
                {
                    // Even the largest candidate cannot hold the largest remaining group: drop it (caller fallback).
                    // 最大候选也装不下剩余的最大组：丢弃该组（调用方回退）。
                    remaining.RemoveAt(0);
                    continue;
                }

                result.Add(assembly);
                var consumed = new HashSet<object>(assembly.GroupTags);
                remaining.RemoveAll(kv => consumed.Contains(kv.Key));
            }
            return result;
        }
    }
}
