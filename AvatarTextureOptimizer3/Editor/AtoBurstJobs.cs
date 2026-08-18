// English: Burst-compiled bitmask dilate used by the atlas packer.
// 中文：供图集装箱使用的 Burst 位掩码膨胀。
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace net.fosa.ato.editor
{
    [BurstCompile]
    public struct AtoDilateJob : IJob
    {
        public int Gw;
        public int Gh;
        public int Radius;
        [ReadOnly] public NativeArray<ulong> Src;
        public NativeArray<ulong> Dst;

        public void Execute()
        {
            int words = (Gw + 63) / 64;
            for (int i = 0; i < Dst.Length; i++) Dst[i] = Src[i];
            for (int y = 0; y < Gh; y++)
            for (int x = 0; x < Gw; x++)
            {
                if (!Get(Src, words, x, y)) continue;
                for (int dy = -Radius; dy <= Radius; dy++)
                for (int dx = -Radius; dx <= Radius; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx < (uint)Gw && (uint)ny < (uint)Gh)
                        Set(Dst, words, nx, ny);
                }
            }
        }

        private static bool Get(NativeArray<ulong> m, int words, int x, int y)
        {
            return (m[y * words + (x >> 6)] & (1UL << (x & 63))) != 0;
        }

        private static void Set(NativeArray<ulong> m, int words, int x, int y)
        {
            int i = y * words + (x >> 6);
            m[i] = m[i] | (1UL << (x & 63));
        }
    }
}
