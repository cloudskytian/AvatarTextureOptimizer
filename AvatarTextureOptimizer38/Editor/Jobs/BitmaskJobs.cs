using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Burst job: rasterize a triangle into a 4px bitmask. / Burst：把三角形光栅化进 4px 位掩码。
    /// </summary>
    [BurstCompile]
    public struct RasterTriJob : IJob
    {
        public int Width, Height;
        public float2 A, B, C;
        public NativeArray<ulong> Words;
        public int WordsPerRow;

        public void Execute()
        {
            int minx = (int)math.floor(math.min(A.x, math.min(B.x, C.x)));
            int maxx = (int)math.ceil(math.max(A.x, math.max(B.x, C.x)));
            int miny = (int)math.floor(math.min(A.y, math.min(B.y, C.y)));
            int maxy = (int)math.ceil(math.max(A.y, math.max(B.y, C.y)));
            minx = math.clamp(minx, 0, Width - 1);
            maxx = math.clamp(maxx, 0, Width - 1);
            miny = math.clamp(miny, 0, Height - 1);
            maxy = math.clamp(maxy, 0, Height - 1);
            for (int y = miny; y <= maxy; y++)
            for (int x = minx; x <= maxx; x++)
            {
                float2 p = new float2(x + 0.5f, y + 0.5f);
                if (!Inside(p, A, B, C)) continue;
                int row = y * WordsPerRow;
                int wi = row + (x >> 6);
                ulong bit = 1UL << (x & 63);
                Words[wi] = Words[wi] | bit;
            }
        }

        static bool Inside(float2 p, float2 a, float2 b, float2 c)
        {
            float s = Sign(p, a, b);
            float t = Sign(p, b, c);
            float u = Sign(p, c, a);
            bool neg = s < 0 || t < 0 || u < 0;
            bool pos = s > 0 || t > 0 || u > 0;
            return !(neg && pos);
        }

        static float Sign(float2 p1, float2 p2, float2 p3) =>
            (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
    }
}
