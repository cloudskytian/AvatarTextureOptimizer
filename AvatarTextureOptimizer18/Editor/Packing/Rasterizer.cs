using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Fosa.AvatarTextureOptimizer.Editor.Packing
{
    // Burst 光栅化：把岛三角形按 4px 粒度光栅化进位掩码（逐岛并行，岛内三角形串行 → 无原子写）。
    // Burst rasterization: island triangles → 4px-granularity bitmask (parallel per island, serial per triangle → no atomics).
    public struct RasterInput
    {
        public int uvStart;      // UV 池中的三角形起点。Triangle start in the UV pool.
        public int triangleCount;
        public float scaleX, scaleY;   // 质量缩放。Quality scale.
        public float texW, texH;       // 贴图分辨率。Texture resolution.
        public int maskW, maskH;       // 掩码尺寸（4px 单元格）。Mask size in cells.
        public int maskOffset;         // 输出池偏移。Output pool offset.
    }

    [BurstCompile]
    public struct RasterizeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> uvPool;    // 每三角形 3 个 float2。3 float2 per triangle.
        [ReadOnly] public NativeArray<RasterInput> inputs;
        public NativeArray<ulong> masks;                  // 输出：每岛 maskW/64 向上取整 × maskH。Output: ceil(maskW/64) × maskH per island.
        public NativeArray<int> rowStrides;               // 每岛每行 ulong 数。Ulongs per row per island.

        public void Execute(int i)
        {
            var input = inputs[i];
            int stride = rowStrides[i];
            int offset = input.maskOffset;

            for (int t = 0; t < input.triangleCount; t++)
            {
                int idx = input.uvStart + t * 3;
                float2 a = Transform(uvPool[idx], in input);
                float2 b = Transform(uvPool[idx + 1], in input);
                float2 c = Transform(uvPool[idx + 2], in input);

                // 4px 单元格中心采样。Sample at 4px cell centers.
                float minX = math.min(math.min(a.x, b.x), c.x);
                float maxX = math.max(math.max(a.x, b.x), c.x);
                float minY = math.min(math.min(a.y, b.y), c.y);
                float maxY = math.max(math.max(a.y, b.y), c.y);
                int cx0 = math.clamp((int)(minX / 4f), 0, input.maskW - 1);
                int cx1 = math.clamp((int)(maxX / 4f), 0, input.maskW - 1);
                int cy0 = math.clamp((int)(minY / 4f), 0, input.maskH - 1);
                int cy1 = math.clamp((int)(maxY / 4f), 0, input.maskH - 1);

                for (int cy = cy0; cy <= cy1; cy++)
                {
                    float py = cy * 4f + 2f;
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        float px = cx * 4f + 2f;
                        if (PointInTriangle(px, py, a, b, c))
                        {
                            int bit = cx & 63;
                            masks[offset + cy * stride + (cx >> 6)] |= 1UL << bit;
                        }
                    }
                }
            }
        }

        private static float2 Transform(float2 uv, in RasterInput input)
        {
            // uv 为归一化坐标；转换到岛局部像素坐标（4px 粒度光栅化输入）。UV is normalized; map to island-local pixels.
            return new float2(uv.x * input.texW * input.scaleX, uv.y * input.texH * input.scaleY);
        }

        private static bool PointInTriangle(float px, float py, float2 a, float2 b, float2 c)
        {
            float d1 = Sign(px, py, a, b);
            float d2 = Sign(px, py, b, c);
            float d3 = Sign(px, py, c, a);
            bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos);
        }

        private static float Sign(float px, float py, float2 a, float2 b)
        {
            return (px - b.x) * (a.y - b.y) - (a.x - b.x) * (py - b.y);
        }
    }
}
