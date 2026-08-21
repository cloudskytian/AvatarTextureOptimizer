using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace net.fosa.ato
{
    // ============================================================================
    // Burst 装箱作业 / Burst packing jobs.
    // 4px 粒度光栅位掩码(行并行, 无原子操作) + 全扫描 Bottom-Left-Fill + padding 膨胀.
    // 4px-granularity raster bitmasks (row-parallel, no atomics) + full-scan BLF + padding dilation.
    // 位掩码每行按 32bit 字存储, 越界移位依赖高位为 0, 位移跨字时拆 lo/hi 两段.
    // Masks are stored as 32-bit words per row; shifted bits beyond the mask width are zero.
    // ============================================================================

    /// <summary>
    /// 按行并行光栅化岛掩码: 每个工作项负责一行, 只写本行的字 -> 无竞争.
    /// Row-parallel island mask rasterization: each work item owns one row -> race-free.
    /// </summary>
    [BurstCompile]
    internal struct ATOBuildMaskJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> triVerts;   // 每岛三角形顶点(3 per tri) / per-island triangle vertices
        [ReadOnly] public NativeArray<float> uvX;      // 网格通道UV / mesh channel UVs
        [ReadOnly] public NativeArray<float> uvY;
        public NativeArray<int> words;                 // 输出掩码字 / output mask words
        public int mw, mh, wWords;                     // 掩码尺寸(格) / mask dims in cells
        public float texW, texH;
        public float rectX, rectY;                     // 贴图像素矩形原点 / pixel-rect origin
        public float scaleX, scaleY;                   // 质量缩放 / quality scale

        public void Execute(int row)
        {
            int triCount = triVerts.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                int v0 = triVerts[t * 3];
                int v1 = triVerts[t * 3 + 1];
                int v2 = triVerts[t * 3 + 2];
                float ax = uvX[v0], ay = uvY[v0];
                float bx = uvX[v1], by = uvY[v1];
                float cx = uvX[v2], cy = uvY[v2];

                float minY = Min3(ay, by, cy), maxY = Max3(ay, by, cy);
                int cy0 = Clampi((int)System.Math.Floor((minY * texH - rectY) / (4f * scaleY)), 0, mh - 1);
                int cy1 = Clampi((int)System.Math.Ceiling((maxY * texH - rectY) / (4f * scaleY)), cy0, mh - 1);
                if (row < cy0 || row > cy1) continue;

                float minX = Min3(ax, bx, cx), maxX = Max3(ax, bx, cx);
                int cx0 = Clampi((int)System.Math.Floor((minX * texW - rectX) / (4f * scaleX)), 0, mw - 1);
                int cx1 = Clampi((int)System.Math.Ceiling((maxX * texW - rectX) / (4f * scaleX)), cx0, mw - 1);

                float yU0 = (rectY + row * 4f * scaleY) / texH;
                float yU1 = (rectY + (row + 1) * 4f * scaleY) / texH;
                for (int cell = cx0; cell <= cx1; cell++)
                {
                    float xU0 = (rectX + cell * 4f * scaleX) / texW;
                    float xU1 = (rectX + (cell + 1) * 4f * scaleX) / texW;
                    if (TriOverlapsRect(ax, ay, bx, by, cx, cy, xU0, xU1, yU0, yU1))
                    {
                        int word = row * wWords + (cell >> 5);
                        words[word] |= 1 << (cell & 31);
                    }
                }
            }
        }

        static bool TriOverlapsRect(float ax, float ay, float bx, float by, float cx, float cy,
            float u0, float u1, float v0, float v1)
        {
            if (Max3(ax, bx, cx) < u0 || Min3(ax, bx, cx) > u1 || Max3(ay, by, cy) < v0 || Min3(ay, by, cy) > v1) return false;
            return PointInTri(u0, v0, ax, ay, bx, by, cx, cy)
                || PointInTri(u1, v0, ax, ay, bx, by, cx, cy)
                || PointInTri(u0, v1, ax, ay, bx, by, cx, cy)
                || PointInTri(u1, v1, ax, ay, bx, by, cx, cy)
                || PointInTri((u0 + u1) * 0.5f, (v0 + v1) * 0.5f, ax, ay, bx, by, cx, cy);
        }

        static bool PointInTri(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
        {
            float d1 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
            float d2 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
            float d3 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        static float Min3(float a, float b, float c) { return a < b ? (a < c ? a : c) : (b < c ? b : c); }
        static float Max3(float a, float b, float c) { return a > b ? (a > c ? a : c) : (b > c ? b : c); }
        static int Clampi(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    /// <summary>
    /// padding 膨胀: out = OR over dr∈[0,2p] rows, dx∈[0,2p] bits of (in << dx). 输出行并行.
    /// Dilation: out = OR over dr∈[0,2p] rows and dx∈[0,2p] bits of (in << dx). Parallel over output rows.
    /// </summary>
    [BurstCompile]
    internal struct ATODilateJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> src;
        public NativeArray<int> dst;
        public int mw, mh;          // 输入尺寸 / input dims
        public int inWords;         // 输入每行字数 / input words per row
        public int pad;             // padding 格数 / padding in cells
        public int outWords;        // 输出每行字数 / output words per row
        public int outW;            // 输出宽(格) / output width in cells

        public void Execute(int row)
        {
            int nw = mw + pad * 2, nh = mh + pad * 2;
            int validBits = (outW & 31) != 0 ? (1 << (outW & 31)) - 1 : -1;
            var rowWords = dst.Slice(row * outWords, outWords);

            int drMax = pad * 2;
            for (int dr = 0; dr <= drMax; dr++)
            {
                int r = row - dr;
                if (r < 0 || r >= mh) continue;
                int dxMax = pad * 2;
                for (int dx = 0; dx <= dxMax; dx++)
                {
                    int dw = dx >> 5, shb = dx & 31;
                    var srcRow = src.Slice(r * inWords, inWords);
                    for (int w = 0; w < inWords; w++)
                    {
                        int m = srcRow[w];
                        if (m == 0) continue;
                        int lo = m << shb;
                        int hi = shb == 0 ? 0 : (int)((uint)m >> (32 - shb));
                        if (w + dw < outWords) rowWords[w + dw] |= lo;
                        if (hi != 0 && w + dw + 1 < outWords) rowWords[w + dw + 1] |= hi;
                    }
                }
            }

            // 屏蔽尾部越界位 / mask stray bits beyond the output width
            int last = outWords - 1;
            rowWords[last] &= validBits;
        }
    }

    /// <summary>
    /// 全扫描 BLF: 从 startX 起扫描, 返回 (x, y) 或 (-1, -1). 单线程作业.
    /// Full-scan Bottom-Left-Fill starting at startX; returns (x, y) or (-1, -1). Single-threaded.
    /// </summary>
    [BurstCompile]
    internal struct ATOBLFJob : IJob
    {
        [ReadOnly] public NativeArray<int> occ;      // 图集占用字 / atlas occupancy words
        [ReadOnly] public NativeArray<int> profile;  // 列高 / column heights
        [ReadOnly] public NativeArray<int> mask;     // 掩码字(已膨胀) / dilated mask words
        public int cells;                            // 图集边长(格) / atlas side in cells
        public int wWordsAtlas;
        public int mw, mh, wWordsMask;
        public int startX;
        public NativeArray<int> result;              // 2 ints: x, y

        public void Execute()
        {
            result[0] = -1;
            result[1] = -1;
            for (int x = startX; x <= cells - mw; x++)
            {
                int y = 0;
                for (int c = 0; c < mw; c++)
                {
                    int p = profile[x + c];
                    if (p > y) y = p;
                }

                if (y + mh > cells) continue;
                if (!Overlaps(x, y))
                {
                    result[0] = x;
                    result[1] = y;
                    return;
                }
            }
        }

        bool Overlaps(int x, int y)
        {
            for (int r = 0; r < mh; r++)
            {
                int baseWord = (y + r) * wWordsAtlas;
                for (int w = 0; w < wWordsMask; w++)
                {
                    int m = mask[r * wWordsMask + w];
                    if (m == 0) continue;
                    int dx = x + w * 32;
                    int dw = dx >> 5, sh = dx & 31;
                    int lo = m << sh;
                    int hi = sh == 0 ? 0 : (int)((uint)m >> (32 - sh));
                    if ((occ[baseWord + dw] & lo) != 0) return true;
                    if (hi != 0 && dw + 1 < wWordsAtlas && (occ[baseWord + dw + 1] & hi) != 0) return true;
                }
            }

            return false;
        }
    }

    /// <summary>固定位置占用检查(跨图集归一化矩形校验) / Fixed-position occupancy check (cross-atlas rect validation).</summary>
    [BurstCompile]
    internal struct ATOCanFitJob : IJob
    {
        [ReadOnly] public NativeArray<int> occ;
        [ReadOnly] public NativeArray<int> mask;
        public int cells, wWordsAtlas, mw, mh, wWordsMask;
        public int x, y;
        public NativeArray<int> result; // 1 int: 1 ok, 0 conflict

        public void Execute()
        {
            result[0] = 1;
            if (x < 0 || y < 0 || x + mw > cells || y + mh > cells) { result[0] = 0; return; }
            for (int r = 0; r < mh; r++)
            {
                int baseWord = (y + r) * wWordsAtlas;
                for (int w = 0; w < wWordsMask; w++)
                {
                    int m = mask[r * wWordsMask + w];
                    if (m == 0) continue;
                    int dx = x + w * 32;
                    int dw = dx >> 5, sh = dx & 31;
                    int lo = m << sh;
                    int hi = sh == 0 ? 0 : (int)((uint)m >> (32 - sh));
                    if ((occ[baseWord + dw] & lo) != 0) { result[0] = 0; return; }
                    if (hi != 0 && dw + 1 < wWordsAtlas && (occ[baseWord + dw + 1] & hi) != 0) { result[0] = 0; return; }
                }
            }
        }
    }

    /// <summary>占用写入 / Occupancy write (single-threaded).</summary>
    [BurstCompile]
    internal struct ATOOccupyJob : IJob
    {
        public NativeArray<int> occ;
        [ReadOnly] public NativeArray<int> mask;
        public int wWordsAtlas, mw, mh, wWordsMask;
        public int x, y;

        public void Execute()
        {
            for (int r = 0; r < mh; r++)
            {
                int baseWord = (y + r) * wWordsAtlas;
                for (int w = 0; w < wWordsMask; w++)
                {
                    int m = mask[r * wWordsMask + w];
                    if (m == 0) continue;
                    int dx = x + w * 32;
                    int dw = dx >> 5, sh = dx & 31;
                    int lo = m << sh;
                    int hi = sh == 0 ? 0 : (int)((uint)m >> (32 - sh));
                    if (dw < wWordsAtlas) occ[baseWord + dw] |= lo;
                    if (hi != 0 && dw + 1 < wWordsAtlas) occ[baseWord + dw + 1] |= hi;
                }
            }
        }
    }

    /// <summary>列高更新(列并行, 每列写不同下标) / Column-height update (column-parallel).</summary>
    [BurstCompile]
    internal struct ATOProfileUpdateJob : IJobParallelFor
    {
        public NativeArray<int> profile;
        [ReadOnly] public NativeArray<int> mask;
        public int mw, mh, wWordsMask;
        public int x, y;

        public void Execute(int c)
        {
            int top = -1;
            for (int r = mh - 1; r >= 0 && top < 0; r--)
            {
                int m = mask[r * wWordsMask + (c >> 5)];
                if ((m & (1 << (c & 31))) != 0) top = r;
            }

            if (top < 0) return;
            int h = y + top + 1;
            int idx = x + c;
            if (profile[idx] < h) profile[idx] = h;
        }
    }
}
