// English: Burst jobs for island rasterization and pixel hashing. Called from the packer / cache paths.
// 中文：岛光栅化与像素哈希的 Burst 作业，供装箱 / 缓存路径调用。
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    [BurstCompile]
    internal struct ATOHashPixelsJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Bytes;
        public NativeArray<ulong> Result;

        public void Execute()
        {
            // xxHash64-ish one-shot for bake-time content identity.
            const ulong prime1 = 11400714785074694791UL;
            const ulong prime2 = 14029467366897019727UL;
            const ulong prime3 = 1609587929392839161UL;
            ulong h = 2870177450012600261UL;
            var i = 0;
            while (i + 8 <= Bytes.Length)
            {
                ulong k = Bytes[i]
                          | ((ulong)Bytes[i + 1] << 8)
                          | ((ulong)Bytes[i + 2] << 16)
                          | ((ulong)Bytes[i + 3] << 24)
                          | ((ulong)Bytes[i + 4] << 32)
                          | ((ulong)Bytes[i + 5] << 40)
                          | ((ulong)Bytes[i + 6] << 48)
                          | ((ulong)Bytes[i + 7] << 56);
                k *= prime2;
                k = Rotate(k, 31);
                k *= prime1;
                h ^= k;
                h = Rotate(h, 27) * prime1 + prime4;
                i += 8;
            }

            for (; i < Bytes.Length; i++)
            {
                h ^= Bytes[i] * prime5;
                h = Rotate(h, 11) * prime1;
            }

            h ^= (ulong)Bytes.Length;
            h ^= h >> 33;
            h *= prime2;
            h ^= h >> 29;
            h *= prime3;
            h ^= h >> 32;
            Result[0] = h;
        }

        private const ulong prime4 = 9650029242287828579UL;
        private const ulong prime5 = 2870177450012600261UL;

        private static ulong Rotate(ulong x, int r)
        {
            return (x << r) | (x >> (64 - r));
        }
    }

    [BurstCompile]
    internal struct ATOFillMaskJob : IJobParallelFor
    {
        public int Width;
        public int Height;
        public NativeArray<byte> Mask;

        public void Execute(int index)
        {
            var y = index / Width;
            var x = index - y * Width;
            if ((uint)x < (uint)Width && (uint)y < (uint)Height) Mask[index] = 1;
        }
    }

    internal static class ATOBurstUtil
    {
        public static ulong HashBytes(byte[] data)
        {
            if (data == null || data.Length == 0) return 0;
            var native = new NativeArray<byte>(data.Length, Allocator.TempJob);
            var result = new NativeArray<ulong>(1, Allocator.TempJob);
            try
            {
                native.CopyFrom(data);
                var job = new ATOHashPixelsJob { Bytes = native, Result = result };
                job.Schedule().Complete();
                return result[0];
            }
            finally
            {
                if (native.IsCreated) native.Dispose();
                if (result.IsCreated) result.Dispose();
            }
        }
    }
}
