// AvatarTextureOptimizer - IslandRasterizer
// EN: Burst rasterization of island triangle coverage into 4px-granularity bit masks (with padding border),
// plus bit-mask transpose (90-degree rotation) — normal maps never recompute tangents (rotating the mask only
// remaps sampling, which is correct for tangent-space data).
// CN: 把岛三角形覆盖区 Burst 光栅化为 4px 粒度位掩码（含 padding 边框），
//     以及位掩码转置（90 度旋转）——法线贴图绝不重算切线（仅旋转掩码重映射采样，对切线空间数据是正确的）。
using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>EN: A 4px-cell bit mask. / CN: 4px 格位掩码。</summary>
    public sealed class CellMask
    {
        public int cellsW, cellsH;          // 单元数
        public NativeArray<ulong> bits;     // cellsW*cellsH/64
        public int wordCount;
        public int areaCells;               // 覆盖单元数
        public RectInt bbox;                // 覆盖包围盒（单元）

        public CellMask(int cellsW, int cellsH)
        {
            this.cellsW = cellsW;
            this.cellsH = cellsH;
            wordCount = (cellsW * cellsH + 63) / 64;
            bits = new NativeArray<ulong>(wordCount, Allocator.Persistent);
            bbox = new RectInt(0, 0, 0, 0);
        }

        public bool Get(int x, int y)
        {
            if (x < 0 || y < 0 || x >= cellsW || y >= cellsH) return false;
            int idx = y * cellsW + x;
            return (bits[idx >> 6] & (1UL << (idx & 63))) != 0;
        }

        public void Dispose() { if (bits.IsCreated) bits.Dispose(); }
    }

    /// <summary>
    /// EN: Rasterizes an island's triangles into a CellMask at 4px granularity, padded by padCells.
    /// CN: 把岛的三角形光栅化为 4px 粒度 CellMask，含 padCells 边框。
    /// </summary>
    public static class IslandRasterizer
    {
        public static CellMask Rasterize(Island island, int pixelW, int pixelH, int padPx, bool rotated)
        {
            int cellsW = Mathf.Max(1, (pixelW + 3) / 4);
            int cellsH = Mathf.Max(1, (pixelH + 3) / 4);
            int pad = Mathf.Max(0, (padPx + 3) / 4);
            int bw = cellsW + pad * 2;
            int bh = cellsH + pad * 2;

            var mask = new CellMask(bw, bh);
            var data = island.owner;
            if (data == null) return mask;

            var uvs = data.uvs;
            var allTris = data.allTriangles;
            if (allTris == null) return mask;

            float rw = Mathf.Max(1e-6f, island.fracRect.width);
            float rh = Mathf.Max(1e-6f, island.fracRect.height);

            var triList = new NativeArray<int>(island.triangles.Count, Allocator.Temp);
            for (int i = 0; i < island.triangles.Count; i++) triList[i] = island.triangles[i];
            var uvArr = new NativeArray<float2>(uvs.Length, Allocator.Temp);
            for (int i = 0; i < uvs.Length; i++) uvArr[i] = new float2(uvs[i].x, uvs[i].y);
            var tris = new NativeArray<int3>(island.triangles.Count, Allocator.Temp);
            for (int i = 0; i < island.triangles.Count; i++)
            {
                int t = island.triangles[i];
                tris[i] = new int3(allTris[t * 3], allTris[t * 3 + 1], allTris[t * 3 + 2]);
            }

            var job = new RasterizeIslandMaskJob
            {
                uvs = uvArr,
                triangles = tris,
                tileX = island.tile.x,
                tileY = island.tile.y,
                rectX = island.fracRect.x,
                rectY = island.fracRect.y,
                rectW = rw,
                rectH = rh,
                pad = pad,
                cellsW = bw,
                cellsH = bh,
                bits = mask.bits,
                rotated = rotated
            };
            job.Schedule().Complete();
            triList.Dispose(); uvArr.Dispose(); tris.Dispose();

            // EN: Compute coverage bbox & area.
            // CN: 计算覆盖包围盒与面积。
            int minX = bw, minY = bh, maxX = -1, maxY = -1, area = 0;
            for (int y = 0; y < bh; y++)
            {
                for (int x = 0; x < bw; x++)
                {
                    if (!mask.Get(x, y)) continue;
                    area++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            mask.areaCells = area;
            if (maxX >= minX) mask.bbox = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return mask;
        }
    }

    [BurstCompile]
    internal struct RasterizeIslandMaskJob : IJob
    {
        [ReadOnly] public NativeArray<float2> uvs;
        [ReadOnly] public NativeArray<int3> triangles;
        public int tileX, tileY;
        public float rectX, rectY, rectW, rectH;
        public int pad;
        public int cellsW, cellsH;
        public bool rotated;
        public NativeArray<ulong> bits;

        public void Execute()
        {
            for (int t = 0; t < triangles.Length; t++)
            {
                int3 tri = triangles[t];
                float2 a = Local(tri.x), b = Local(tri.y), c = Local(tri.z);
                if (rotated)
                {
                    a = new float2(a.y, 1f - a.x);
                    b = new float2(b.y, 1f - b.x);
                    c = new float2(c.y, 1f - c.x);
                }
                RasterTriangle(a, b, c);
            }
        }

        private float2 Local(int vidx)
        {
            float2 raw = uvs[vidx];
            float lx = raw.x - tileX;
            float ly = raw.y - tileY;
            float nx = (lx - rectX) / rectW;
            float ny = (ly - rectY) / rectH;
            return new float2(nx * cellsW, ny * cellsH);
        }

        private void RasterTriangle(float2 a, float2 b, float2 c)
        {
            float minX = math.max(0, math.min(a.x, math.min(b.x, c.x)) - 1);
            float maxX = math.min(cellsW - 1, math.max(a.x, math.max(b.x, c.x)) + 1);
            float minY = math.max(0, math.min(a.y, math.min(b.y, c.y)) - 1);
            float maxY = math.min(cellsH - 1, math.max(a.y, math.max(b.y, c.y)) + 1);
            if (maxX < minX || maxY < minY) return;

            float bias = 0.5f;
            for (int y = (int)minY; y <= (int)maxY; y++)
            {
                for (int x = (int)minX; x <= (int)maxX; x++)
                {
                    float2 p = new float2(x + 0.5f, y + 0.5f);
                    float e1 = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
                    float e2 = (c.x - b.x) * (p.y - b.y) - (c.y - b.y) * (p.x - b.x);
                    float e3 = (a.x - c.x) * (p.y - c.y) - (a.y - c.y) * (p.x - a.x);
                    bool inside = (e1 >= -bias && e2 >= -bias && e3 >= -bias) ||
                                  (e1 <= bias && e2 <= bias && e3 <= bias);
                    if (!inside) continue;
                    int idx = y * cellsW + x;
                    bits[idx >> 6] |= 1UL << (idx & 63);
                }
            }
        }
    }

    /// <summary>
    /// EN: Bit-mask transpose (90-degree rotation for the packer).
    /// CN: 位掩码转置（装箱器的 90 度旋转）。
    /// </summary>
    [BurstCompile]
    internal struct TransposeMaskJob : IJob
    {
        [ReadOnly] public NativeArray<ulong> src;
        public int srcW, srcH;      // 单元尺寸
        public NativeArray<ulong> dst;
        public int dstW, dstH;

        public void Execute()
        {
            for (int y = 0; y < srcH; y++)
            {
                for (int x = 0; x < srcW; x++)
                {
                    int si = y * srcW + x;
                    if ((src[si >> 6] & (1UL << (si & 63))) == 0) continue;
                    int dx = y;             // transpose: (x,y) -> (y, x)
                    int dy = x;
                    int di = dy * dstW + dx;
                    dst[di >> 6] |= 1UL << (di & 63);
                }
            }
        }
    }
}
