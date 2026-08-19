// PackingJobs.cs
// Burst jobs for atlas packing: bottom-left-full scan placement test against a bit
// occupancy grid. / 图集装箱 Burst 作业:对位占位网格做底左全扫描放置测试。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace net.fosa.ato
{
    /// <summary>
    /// Scans all (x,y) cell positions bottom-left-first and reports the first non-overlapping
    /// placement of a mask against the occupancy grid. / 自底向上、每行自左向右全扫描,
    /// 返回掩码不与占位网格重叠的第一个位置。
    /// </summary>
    [BurstCompile]
    internal struct BlfScanJob : IJob
    {
        [ReadOnly] public NativeArray<ulong> Occupancy;
        public int OccStride;      // words per occupancy row / 占位行字数
        public int OccW, OccH;     // occupancy cells / 占位格数
        [ReadOnly] public NativeArray<ulong> Mask;
        public int MaskStride;
        public int MaskW, MaskH;   // mask cells / 掩码格数
        public int PadCells;       // extra clearance in cells (already in occupancy) / 已含在占位中的间隙格数

        public NativeArray<int> Result; // [0]=found(1/0) [1]=x [2]=y

        public void Execute()
        {
            for (int y = 0; y + MaskH <= OccH; y++)
            {
                for (int x = 0; x + MaskW <= OccW; x++)
                {
                    if (!Overlaps(x, y))
                    {
                        Result[0] = 1; Result[1] = x; Result[2] = y;
                        return;
                    }
                }
            }
            Result[0] = 0; Result[1] = 0; Result[2] = 0;
        }

        private bool Overlaps(int px, int py)
        {
            int wordIdx = px >> 6;
            int bitOff = px & 63;
            for (int r = 0; r < MaskH; r++)
            {
                int occRow = (py + r) * OccStride + wordIdx;
                for (int w = 0; w < MaskStride; w++)
                {
                    ulong m = Mask[r * MaskStride + w];
                    if (m == 0) continue;
                    int idx = occRow + w;
                    if (idx < 0 || idx + (bitOff > 0 ? 1 : 0) >= Occupancy.Length) continue;
                    ulong o0 = Occupancy[idx];
                    if ((o0 & (m << bitOff)) != 0) return true;
                    if (bitOff > 0)
                    {
                        ulong o1 = idx + 1 < Occupancy.Length ? Occupancy[idx + 1] : 0;
                        if ((o1 & (m >> (64 - bitOff))) != 0) return true;
                    }
                }
            }
            return false;
        }
    }
}
