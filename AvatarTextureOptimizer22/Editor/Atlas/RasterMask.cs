// AvatarTextureOptimizer
// File: Editor/Atlas/RasterMask.cs
//
// Occupancy bitmask used by the packer. One bit per 4px grid cell.
//   - island shapes are rasterized from their UV triangles (edge-function
//     fill) at 4px granularity — NOT rectangles (spec)
//   - placement search (full-scan bottom-left-first) runs in a Burst job
//
// 装箱器使用的占用位掩码。每个 4px 网格单元一个 bit。
//   - 岛形状从 UV 三角形光栅化（边函数填充）到 4px 粒度——不是矩形（规格）
//   - 放置搜索（全扫描左下优先）在 Burst 任务中运行

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.atlas
{
    /// <summary>
    /// A bitmask grid; 1 bit per cell. Used for both atlas occupancy and island
    /// shapes. / 位掩码网格；每单元 1 bit。用于图集占用与岛形状。
    /// </summary>
    public sealed class RasterMask
    {
        public readonly int WidthCells;   // grid cells (pixels / granularity) / 网格单元数（像素/粒度）
        public readonly int HeightCells;
        private readonly byte[] _bits;

        public RasterMask(int widthCells, int heightCells)
        {
            WidthCells = widthCells;
            HeightCells = heightCells;
            _bits = new byte[(long)widthCells * heightCells / 8 + 1];
        }

        private int ByteIndex(int x, int y) => y * WidthCells + x;
        private int BitIndex(int x) => x & 7;

        public bool Get(int x, int y)
        {
            if (x < 0 || y < 0 || x >= WidthCells || y >= HeightCells) return true; // outside = occupied / 外部视为占用
            int idx = ByteIndex(x, y);
            return (_bits[idx >> 3] & (1 << BitIndex(x))) != 0;
        }

        public void Set(int x, int y)
        {
            if (x < 0 || y < 0 || x >= WidthCells || y >= HeightCells) return;
            int idx = ByteIndex(x, y);
            _bits[idx >> 3] |= (byte)(1 << BitIndex(x));
        }

        public byte[] RawBits => _bits;
        public int ByteCount => _bits.Length;

        /// <summary>
        /// Rasterize a set of UV triangles into this mask (clearing first).
        /// Triangles are given in grid coordinates (already normalized to the
        /// mask's cell space). Edge-function fill at cell centers.
        /// 将一组 UV 三角形光栅化进该掩码（先清空）。三角形以网格坐标给出
        /// （已归一化到掩码的单元空间）。在单元中心做边函数填充。
        /// </summary>
        public void RasterizeTriangles(float[] triGridCoords, bool clearFirst = true)
        {
            if (clearFirst) Array.Clear(_bits, 0, _bits.Length);
            int triCount = triGridCoords.Length / 6;
            for (int t = 0; t < triCount; t++)
            {
                int o = t * 6;
                float x0 = triGridCoords[o], y0 = triGridCoords[o + 1];
                float x1 = triGridCoords[o + 2], y1 = triGridCoords[o + 3];
                float x2 = triGridCoords[o + 4], y2 = triGridCoords[o + 5];
                FillTriangle(x0, y0, x1, y1, x2, y2);
            }
        }

        private void FillTriangle(float x0, float y0, float x1, float y1, float x2, float y2)
        {
            float minX = Mathf.Max(0, Mathf.Floor(Mathf.Min(x0, Mathf.Min(x1, x2))));
            float maxX = Mathf.Min(WidthCells - 1, Mathf.Ceil(Mathf.Max(x0, Mathf.Max(x1, x2))));
            float minY = Mathf.Max(0, Mathf.Floor(Mathf.Min(y0, Mathf.Min(y1, y2))));
            float maxY = Mathf.Min(HeightCells - 1, Mathf.Ceil(Mathf.Max(y0, Mathf.Max(y1, y2))));

            float e0 = Edge(x0, y0, x1, y1);
            float e1 = Edge(x1, y1, x2, y2);
            float e2 = Edge(x2, y2, x0, y0);
            bool flip = e0 < 0 || e1 < 0 || e2 < 0;

            for (int y = (int)minY; y <= (int)maxY; y++)
            {
                for (int x = (int)minX; x <= (int)maxX; x++)
                {
                    float cx = x + 0.5f, cy = y + 0.5f;
                    float d0 = Edge(x0, y0, x1, y1) * (flip ? -1 : 1);
                    float d1 = Edge(x1, y1, x2, y2) * (flip ? -1 : 1);
                    float d2 = Edge(x2, y2, x0, y0) * (flip ? -1 : 1);
                    if (d0 >= 0 && d1 >= 0 && d2 >= 0) Set(x, y);
                }
            }
            _ = e0; _ = e1; _ = e2;
        }

        private static float Edge(float ax, float ay, float bx, float by) => (bx - ax) * (ay - 0) - (by - ay) * (ax - 0);

        /// <summary>Transposed copy (90-degree rotation of the shape). / 转置副本（形状旋转 90 度）。</summary>
        public RasterMask Transposed()
        {
            var t = new RasterMask(HeightCells, WidthCells);
            for (int y = 0; y < HeightCells; y++)
                for (int x = 0; x < WidthCells; x++)
                    if (Get(x, y)) t.Set(y, x);
            return t;
        }

        /// <summary>
        /// Burst full-scan bottom-left-first placement search.
        /// Returns true and fills (outX, outY) when a clear position is found.
        /// Burst 全扫描左下优先放置搜索。找到空位时返回 true 并填充 (outX, outY)。
        /// </summary>
        public bool FindPlacement(RasterMask island, out int outX, out int outY)
        {
            outX = 0; outY = 0;
            if (island.WidthCells > WidthCells || island.HeightCells > HeightCells) return false;

            using var atlasBits = new NativeArray<byte>(_bits, Allocator.TempJob);
            using var islandBits = new NativeArray<byte>(island._bits, Allocator.TempJob);
            var result = new NativeArray<int>(3, Allocator.TempJob); // (found, x, y)
            var job = new PlacementSearchJob
            {
                AtlasBits = atlasBits,
                IslandBits = islandBits,
                AtlasW = WidthCells,
                AtlasH = HeightCells,
                IslandW = island.WidthCells,
                IslandH = island.HeightCells,
                Result = result,
            };
            job.Run();
            bool found = result[0] != 0;
            if (found)
            {
                outX = result[1];
                outY = result[2];
            }
            return found;
        }

        public void Or(RasterMask other, int offsetX, int offsetY)
        {
            for (int y = 0; y < other.HeightCells; y++)
                for (int x = 0; x < other.WidthCells; x++)
                    if (other.Get(x, y)) Set(x + offsetX, y + offsetY);
        }
    }

    /// <summary>
    /// Burst job: find the first (x from left, y from bottom) placement where
    /// the island mask does not intersect the atlas mask.
    /// Burst 任务：找到第一个（x 从左、y 从下）岛掩码与图集掩码不相交的放置。
    /// </summary>
    [BurstCompile]
    internal struct PlacementSearchJob : IJob
    {
        [ReadOnly] public NativeArray<byte> AtlasBits;
        [ReadOnly] public NativeArray<byte> IslandBits;
        public int AtlasW, AtlasH, IslandW, IslandH;
        [WriteOnly] public NativeArray<int> Result; // (found, x, y)

        public void Execute()
        {
            for (int y = 0; y <= AtlasH - IslandH; y++)
            {
                for (int x = 0; x <= AtlasW - IslandW; x++)
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

        private bool Overlaps(int ox, int oy)
        {
            int atlasRowBytes = (AtlasW + 7) >> 3;
            int islandRowBytes = (IslandW + 7) >> 3;
            for (int y = 0; y < IslandH; y++)
            {
                int islandRowStart = y * islandRowBytes;
                int atlasRowStart = (oy + y) * atlasRowBytes;
                // Bit offset of this island row inside the atlas row.
                // 该岛行在图集行内的位偏移。
                int bitOffset = ox;
                for (int p = 0; p < islandRowBytes; p++)
                {
                    byte ib = IslandBits[islandRowStart + p];
                    if (ib == 0) { bitOffset += 8; continue; }

                    int byteIdx = bitOffset >> 3;
                    int bitShift = bitOffset & 7;
                    if (byteIdx >= atlasRowBytes) return false; // past the row end / 越过行尾

                    byte a1 = AtlasBits[atlasRowStart + byteIdx];
                    if (bitShift == 0)
                    {
                        if ((ib & a1) != 0) return true;
                    }
                    else
                    {
                        byte partLow = (byte)(ib << bitShift);
                        if ((partLow & a1) != 0) return true;
                        byte partHigh = (byte)(ib >> (8 - bitShift));
                        if (partHigh != 0 && byteIdx + 1 < atlasRowBytes &&
                            (partHigh & AtlasBits[atlasRowStart + byteIdx + 1]) != 0)
                            return true;
                    }
                    bitOffset += 8;
                }
            }
            return false;
        }
    }
}
