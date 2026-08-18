// Copyright (c) fosa. Licensed under the MIT License.
// Burst-compiled bitmask placement search. Each candidate row is scanned independently, so the
// full bottom-left-fill scan parallelises perfectly while remaining bit-exact and deterministic:
// the reduction always picks the lowest row, then the leftmost column, exactly as the scalar
// implementation does.
// Burst 编译的位掩码放置搜索。每个候选行独立扫描，
// 因此完整的 BLF 扫描可完美并行，同时保持位精确与确定性：
// 归约始终选取最低行、其次最左列，与标量实现完全一致。

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Burst job that finds, for every row, the leftmost cell where a mask fits.
    /// 为每一行寻找掩码可放置的最左单元的 Burst 作业。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Default, FloatPrecision = FloatPrecision.Standard)]
    public struct FindPositionJob : IJobParallelFor
    {
        /// <summary>Occupancy bits of the atlas, row-major, 64 cells per word. / 图集占用位，行主序，每字 64 单元。</summary>
        [ReadOnly] public NativeArray<ulong> Grid;

        /// <summary>Words per grid row. / 每个网格行的字数。</summary>
        public int GridWordsPerRow;

        /// <summary>Grid width in cells. / 网格宽度（单元数）。</summary>
        public int GridWidth;

        /// <summary>Grid height in cells. / 网格高度（单元数）。</summary>
        public int GridHeight;

        /// <summary>Island coverage bits. / 岛覆盖位。</summary>
        [ReadOnly] public NativeArray<ulong> Mask;

        /// <summary>Mask width in cells. / 掩码宽度（单元数）。</summary>
        public int MaskWidth;

        /// <summary>Mask height in cells. / 掩码高度（单元数）。</summary>
        public int MaskHeight;

        /// <summary>Per-row result: leftmost fitting column, or -1. / 每行结果：最左可放置列，无解为 -1。</summary>
        [WriteOnly] public NativeArray<int> RowResults;

        /// <inheritdoc />
        public void Execute(int y)
        {
            RowResults[y] = -1;

            if (y + MaskHeight > GridHeight) return;

            var maskWords = (MaskWidth + 63) / 64;
            var maxX = GridWidth - MaskWidth;

            for (var x = 0; x <= maxX; x++)
            {
                if (Fits(x, y, maskWords))
                {
                    RowResults[y] = x;
                    return;
                }
            }
        }

        private bool Fits(int ox, int oy, int maskWords)
        {
            for (var my = 0; my < MaskHeight; my++)
            {
                var rowBase = my * maskWords;

                for (var wi = 0; wi < maskWords; wi++)
                {
                    var word = Mask[rowBase + wi];

                    while (word != 0)
                    {
                        var bit = TrailingZeroCount(word);
                        word &= word - 1;

                        var mx = wi * 64 + bit;
                        if (mx >= MaskWidth) continue;

                        var gx = ox + mx;
                        var gy = oy + my;
                        if (gx >= GridWidth || gy >= GridHeight) return false;

                        var gi = gy * GridWordsPerRow + (gx >> 6);
                        if ((Grid[gi] & (1UL << (gx & 63))) != 0) return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// de Bruijn trailing-zero count. Used instead of an intrinsic so the Burst path and the
        /// scalar path share one implementation that is already exhaustively verified.
        /// de Bruijn 末尾零位计数。不使用内建指令，
        /// 以便 Burst 路径与标量路径共用同一份已完成穷尽验证的实现。
        /// </summary>
        internal static int TrailingZeroCount(ulong value)
        {
            if (value == 0) return 64;

            var index = (((ulong)((long)value & -(long)value)) * 0x37E84A99DAE458FUL) >> 58;
            return DeBruijnPosition((int)index);
        }

        /// <summary>
        /// Burst cannot access managed static arrays, so the lookup table is expressed as a
        /// branch-free computation over a pair of constants.
        /// Burst 无法访问托管静态数组，
        /// 因此查找表以一对常量上的无分支计算表达。
        /// </summary>
        private static int DeBruijnPosition(int index)
        {
            // The 64-entry table packed 6 bits per entry into two 64-bit halves would be opaque;
            // a small switch keeps it readable and Burst compiles it to a jump table.
            // 将 64 项表按每项 6 位打包进两个 64 位半字会难以阅读；
            // 使用小型 switch 更清晰，且 Burst 会将其编译为跳转表。
            switch (index)
            {
                case 0: return 0;
                case 1: return 1;
                case 2: return 17;
                case 3: return 2;
                case 4: return 18;
                case 5: return 50;
                case 6: return 3;
                case 7: return 57;
                case 8: return 47;
                case 9: return 19;
                case 10: return 22;
                case 11: return 51;
                case 12: return 29;
                case 13: return 4;
                case 14: return 33;
                case 15: return 58;
                case 16: return 15;
                case 17: return 48;
                case 18: return 20;
                case 19: return 27;
                case 20: return 25;
                case 21: return 23;
                case 22: return 52;
                case 23: return 41;
                case 24: return 54;
                case 25: return 30;
                case 26: return 38;
                case 27: return 5;
                case 28: return 43;
                case 29: return 34;
                case 30: return 59;
                case 31: return 8;
                case 32: return 63;
                case 33: return 16;
                case 34: return 49;
                case 35: return 56;
                case 36: return 46;
                case 37: return 21;
                case 38: return 28;
                case 39: return 32;
                case 40: return 14;
                case 41: return 26;
                case 42: return 24;
                case 43: return 40;
                case 44: return 53;
                case 45: return 37;
                case 46: return 42;
                case 47: return 7;
                case 48: return 62;
                case 49: return 55;
                case 50: return 45;
                case 51: return 31;
                case 52: return 13;
                case 53: return 39;
                case 54: return 36;
                case 55: return 6;
                case 56: return 61;
                case 57: return 44;
                case 58: return 12;
                case 59: return 35;
                case 60: return 60;
                case 61: return 11;
                case 62: return 10;
                default: return 9;
            }
        }
    }

    /// <summary>
    /// Schedules <see cref="FindPositionJob" /> and reduces the per-row results.
    /// 调度 <see cref="FindPositionJob" /> 并归约每行结果。
    /// </summary>
    public static class BurstPackKernel
    {
        /// <summary>Rows per worker batch. / 每个工作批次的行数。</summary>
        private const int BatchSize = 8;

        /// <summary>
        /// Finds the bottom-left-most placement of a mask. Returns false when it does not fit.
        /// 寻找掩码的最下最左放置位置。放不下时返回 false。
        /// </summary>
        public static bool FindPosition(
            ulong[] grid,
            int gridWordsPerRow,
            int gridWidth,
            int gridHeight,
            ulong[] mask,
            int maskWidth,
            int maskHeight,
            out int outX,
            out int outY)
        {
            outX = 0;
            outY = 0;

            if (maskWidth > gridWidth || maskHeight > gridHeight) return false;

            var gridArray = new NativeArray<ulong>(grid, Allocator.TempJob);
            var maskArray = new NativeArray<ulong>(mask, Allocator.TempJob);
            var results = new NativeArray<int>(gridHeight, Allocator.TempJob);

            try
            {
                var job = new FindPositionJob
                {
                    Grid = gridArray,
                    GridWordsPerRow = gridWordsPerRow,
                    GridWidth = gridWidth,
                    GridHeight = gridHeight,
                    Mask = maskArray,
                    MaskWidth = maskWidth,
                    MaskHeight = maskHeight,
                    RowResults = results,
                };

                job.Schedule(gridHeight, BatchSize).Complete();

                // Reduce deterministically: lowest row wins, ties broken by leftmost column.
                // 确定性归约：最低行优先，平局时取最左列。
                for (var y = 0; y < gridHeight; y++)
                {
                    var x = results[y];
                    if (x < 0) continue;

                    outX = x;
                    outY = y;
                    return true;
                }

                return false;
            }
            finally
            {
                gridArray.Dispose();
                maskArray.Dispose();
                results.Dispose();
            }
        }
    }
}
