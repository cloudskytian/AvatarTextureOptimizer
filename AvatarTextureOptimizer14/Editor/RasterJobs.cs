// RasterJobs — Burst bitmask rasterization & BLF bits / Burst 位掩码光栅化与 BLF 位运算
// 4px-granularity shape masks (no rectangle packing, per spec): rasterize triangle fill at cell
// centers, chebyshev dilation for padding, transpose = 90° rotation, word-level BLF fit.<br>
// 4px 粒度岛形状位掩码（不用矩形装箱）：单元中心三角形填充、padding 切比雪夫膨胀、转置=90°旋转、字级 BLF 匹配。
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Fosa.ATO.Editor
{
    internal static class RasterJobs
    {
        internal const int CellPx = 4; // 4px granularity / 4px 粒度

        internal static int WordsFor(int cellW) => (cellW + 63) / 64;

        /// <summary>Rasterize island triangles into a cell mask. / 将岛三角形光栅化为单元掩码。</summary>
        [BurstCompile]
        internal struct RasterIslandJob : IJob
        {
            [ReadOnly] public NativeArray<float> corners; // uv xy * 3 per tri, in cell units / 单元坐标系uv三元组
            public int cellW, cellH;
            public NativeArray<ulong> mask; // WordsFor(cellW) * cellH

            public void Execute()
            {
                int words = WordsFor(cellW);
                for (int t = 0; t < corners.Length / 6; t++)
                {
                    float ax = corners[t * 6], ay = corners[t * 6 + 1];
                    float bx = corners[t * 6 + 2], by = corners[t * 6 + 3];
                    float cx = corners[t * 6 + 4], cy = corners[t * 6 + 5];
                    int minX = ClampI((int)UnityEngine.Mathf.Floor(Min3(ax, bx, cx)), 0, cellW - 1);
                    int maxX = ClampI((int)UnityEngine.Mathf.Ceil(Max3(ax, bx, cx)), 0, cellW - 1);
                    int minY = ClampI((int)UnityEngine.Mathf.Floor(Min3(ay, by, cy)), 0, cellH - 1);
                    int maxY = ClampI((int)UnityEngine.Mathf.Ceil(Max3(ay, by, cy)), 0, cellH - 1);
                    for (int y = minY; y <= maxY; y++)
                    {
                        float py = y + 0.5f;
                        for (int x = minX; x <= maxX; x++)
                        {
                            float px = x + 0.5f;
                            float d0 = Edge(ax, ay, bx, by, px, py);
                            float d1 = Edge(bx, by, cx, cy, px, py);
                            float d2 = Edge(cx, cy, ax, ay, px, py);
                            bool hasNeg = d0 < 0 || d1 < 0 || d2 < 0;
                            bool hasPos = d0 > 0 || d1 > 0 || d2 > 0;
                            if (!(hasNeg && hasPos)) mask[y * words + (x >> 6)] |= 1UL << (x & 63);
                        }
                    }
                }
            }

            private static float Edge(float ax, float ay, float bx, float by, float px, float py) =>
                (px - ax) * (by - ay) - (py - ay) * (bx - ax);
            private static float Min3(float a, float b, float c) => a < b ? (a < c ? a : c) : (b < c ? b : c);
            private static float Max3(float a, float b, float c) => a > b ? (a > c ? a : c) : (b > c ? b : c);
            private static int ClampI(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        }

        /// <summary>Chebyshev dilation by one cell (3×3 OR). / 单元级切比雪夫膨胀（3×3）。</summary>
        [BurstCompile]
        internal struct Dilate3Job : IJob
        {
            [ReadOnly] public NativeArray<ulong> src;
            public int cellW, cellH;
            public NativeArray<ulong> dst;

            public void Execute()
            {
                int words = WordsFor(cellW);
                for (int y = 0; y < cellH; y++)
                for (int w = 0; w < words; w++)
                {
                    ulong acc = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= cellH) continue;
                        ulong v = src[yy * words + w];
                        acc |= v | (v << 1) | (v >> 1);
                        // cross-word bits / 跨字位
                        if (w > 0) { ulong pv = src[yy * words + w - 1]; acc |= pv >> 63; }
                        if (w + 1 < words) { ulong nv = src[yy * words + w + 1]; acc |= nv << 63; }
                    }
                    // mask off out-of-range bits in last word / 末字越界位清零
                    int valid = cellW - w * 64;
                    if (valid < 64) acc &= (1UL << valid) - 1UL;
                    dst[y * words + w] = acc;
                }
            }
        }

        /// <summary>90° rotation via bit transpose. / 位掩码转置实现90°旋转。</summary>
        [BurstCompile]
        internal struct TransposeJob : IJob
        {
            [ReadOnly] public NativeArray<ulong> src;
            public int cellW, cellH;
            public NativeArray<ulong> dst; // dims: cellH × cellW

            public void Execute()
            {
                int srcWords = WordsFor(cellW);
                int dstWords = WordsFor(cellH);
                for (int y = 0; y < cellH; y++)
                for (int w = 0; w < srcWords; w++)
                {
                    ulong v = src[y * srcWords + w];
                    while (v != 0)
                    {
                        int bit = CountTrailingZeros(v);
                        v &= v - 1;
                        int x = w * 64 + bit;
                        // rotated: (x,y) → (cellH-1-y, x) reads as transpose into swapped dims / 旋转映射
                        int nx = y, ny = x; // pure transpose (orientation-agnostic for packing) / 纯转置
                        dst[ny * dstWords + (nx >> 6)] |= 1UL << (nx & 63);
                    }
                }
            }
            private static int CountTrailingZeros(ulong v)
            {
                int n = 0;
                while ((v & 1UL) == 0) { v >>= 1; n++; }
                return n;
            }
        }

        /// <summary>Find first bottom-left position where mask fits; -1 if none. / 全扫描找首个BLF可放位置。</summary>
        [BurstCompile]
        internal struct FindFitJob : IJob
        {
            [ReadOnly] public NativeArray<ulong> canvas;
            public int canvasW, canvasH;
            [ReadOnly] public NativeArray<ulong> mask;
            public int maskW, maskH;
            public NativeArray<int> result; // [0]=x, [1]=y, [2]=found

            public void Execute()
            {
                int cw = WordsFor(canvasW);
                int mw = WordsFor(maskW);
                for (int y = 0; y <= canvasH - maskH; y++)
                for (int x = 0; x <= canvasW - maskW; x++)
                {
                    if (Fits(x, y, cw, mw))
                    {
                        result[0] = x; result[1] = y; result[2] = 1;
                        return;
                    }
                }
                result[2] = 0;
            }

            private bool Fits(int x, int y, int cw, int mw)
            {
                int bitOff = x & 63;
                for (int my = 0; my < maskH; my++)
                {
                    long rowBase = (long)(y + my) * cw;
                    int wordX = x >> 6;
                    for (int w = 0; w < mw; w++)
                    {
                        ulong m = mask[my * mw + w];
                        if (m == 0) continue;
                        int cwx = wordX + w;
                        ulong g0 = canvas[(int)(rowBase + cwx)] >> bitOff;
                        ulong g1 = 0;
                        if (bitOff != 0)
                        {
                            if (cwx + 1 < cw) g1 = canvas[(int)(rowBase + cwx + 1)] << (64 - bitOff);
                        }
                        if (((g0 | g1) & m) != 0) return false;
                    }
                }
                return true;
            }
        }

        /// <summary>OR mask into canvas at (x,y). / 将掩码按位并入画布。</summary>
        [BurstCompile]
        internal struct StampJob : IJob
        {
            public NativeArray<ulong> canvas;
            public int canvasW;
            [ReadOnly] public NativeArray<ulong> mask;
            public int maskW, maskH;
            public int posX, posY;

            public void Execute()
            {
                int cw = WordsFor(canvasW);
                int mw = WordsFor(maskW);
                int bitOff = posX & 63;
                int wordX = posX >> 6;
                for (int my = 0; my < maskH; my++)
                {
                    long rowBase = (long)(posY + my) * cw;
                    for (int w = 0; w < mw; w++)
                    {
                        ulong m = mask[my * mw + w];
                        if (m == 0) continue;
                        canvas[(int)(rowBase + wordX + w)] |= m << bitOff;
                        if (bitOff != 0 && wordX + w + 1 < cw)
                            canvas[(int)(rowBase + wordX + w + 1)] |= m >> (64 - bitOff);
                    }
                }
            }
        }
    }
}

namespace Fosa.ATO.Editor
{
    internal static partial class RasterJobs2
    {
        /// <summary>Check mask fit at an exact position (overlap = true when colliding). / 定点 FIT 检测。</summary>
        [BurstCompile]
        internal struct CheckFitJob : IJob
        {
            [ReadOnly] public NativeArray<ulong> canvas;
            public int canvasCellW, canvasCellH;
            [ReadOnly] public NativeArray<ulong> mask;
            public int maskCellW, maskCellH;
            public int posX, posY;
            public NativeArray<int> result; // [0]=1 fits

            public void Execute()
            {
                result[0] = Fits() ? 1 : 0;
            }

            private bool Fits()
            {
                if (posX < 0 || posY < 0 || posX + maskCellW > canvasCellW || posY + maskCellH > canvasCellH) return false;
                int cw = (canvasCellW + 63) / 64;
                int mw = (maskCellW + 63) / 64;
                int bitOff = posX & 63;
                int wordX = posX >> 6;
                for (int my = 0; my < maskCellH; my++)
                {
                    long rowBase = (long)(posY + my) * cw;
                    for (int w = 0; w < mw; w++)
                    {
                        ulong m = mask[my * mw + w];
                        if (m == 0) continue;
                        int cwx = wordX + w;
                        ulong g0 = canvas[(int)(rowBase + cwx)] >> bitOff;
                        ulong g1 = (bitOff != 0 && cwx + 1 < cw) ? canvas[(int)(rowBase + cwx + 1)] << (64 - bitOff) : 0UL;
                        if (((g0 | g1) & m) != 0) return false;
                    }
                }
                return true;
            }
        }
    }
}
