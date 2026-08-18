using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Burst triangle raster into 4px bitmask. / Burst 三角形光栅到位掩码。
    /// </summary>
    [BurstCompile]
    public struct AtoRasterJob : IJob
    {
        public int RasterW, RasterH, Words, Granule;
        public float U0, V0, Uw, Vh, Dw, Dh;
        [ReadOnly] public NativeArray<float2> Uv;
        [ReadOnly] public NativeArray<int> Tris; // flattened i0,i1,i2 per island tri
        public NativeArray<ulong> Mask;

        public void Execute()
        {
            int triCount = Tris.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                var a = ToPix(Uv[Tris[t * 3]]);
                var b = ToPix(Uv[Tris[t * 3 + 1]]);
                var c = ToPix(Uv[Tris[t * 3 + 2]]);
                Raster(a, b, c);
            }
        }

        float2 ToPix(float2 uv)
        {
            return new float2((uv.x - U0) / Uw * Dw, (uv.y - V0) / Vh * Dh);
        }

        void Raster(float2 a, float2 b, float2 c)
        {
            int minX = (int)math.floor(math.min(a.x, math.min(b.x, c.x)) / Granule);
            int maxX = (int)math.ceil(math.max(a.x, math.max(b.x, c.x)) / Granule);
            int minY = (int)math.floor(math.min(a.y, math.min(b.y, c.y)) / Granule);
            int maxY = (int)math.ceil(math.max(a.y, math.max(b.y, c.y)) / Granule);
            minX = math.clamp(minX, 0, RasterW - 1);
            maxX = math.clamp(maxX, 0, RasterW - 1);
            minY = math.clamp(minY, 0, RasterH - 1);
            maxY = math.clamp(maxY, 0, RasterH - 1);
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                var p = new float2((x + 0.5f) * Granule, (y + 0.5f) * Granule);
                if (Inside(p, a, b, c))
                {
                    int i = y * Words + (x >> 6);
                    if ((uint)i < (uint)Mask.Length)
                        Mask[i] |= 1UL << (x & 63);
                }
            }
        }

        static bool Inside(float2 p, float2 a, float2 b, float2 c)
        {
            float s = Sign(p, a, b), t = Sign(p, b, c), u = Sign(p, c, a);
            return (s >= 0 && t >= 0 && u >= 0) || (s <= 0 && t <= 0 && u <= 0);
        }

        static float Sign(float2 p, float2 a, float2 b) =>
            (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
    }

    [BurstCompile]
    public struct AtoTransposeJob : IJob
    {
        public int W, H;
        [ReadOnly] public NativeArray<ulong> Src;
        public NativeArray<ulong> Dst;

        public void Execute()
        {
            int sw = (W + 63) / 64;
            int dw = (H + 63) / 64;
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * sw + (x >> 6);
                if ((Src[i] & (1UL << (x & 63))) == 0) continue;
                Dst[x * dw + (y >> 6)] |= 1UL << (y & 63);
            }
        }
    }
}
