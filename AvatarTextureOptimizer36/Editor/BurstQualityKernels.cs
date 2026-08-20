using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Burst kernels for hot quality loops; Unity GPU readback is used by TexturePixelReader when needed. / 质量热点循环使用 Burst；不可读纹理由 TexturePixelReader 使用 Unity GPU 读回。
    /// </summary>
    internal static class BurstQualityKernels
    {
        public static float MeanSquaredError(float[] left, float[] right)
        {
            if (left == null || right == null || left.Length == 0 || left.Length != right.Length) return 0f;
            NativeArray<float> leftNative = new NativeArray<float>(left, Allocator.TempJob);
            NativeArray<float> rightNative = new NativeArray<float>(right, Allocator.TempJob);
            NativeArray<float> result = new NativeArray<float>(left.Length, Allocator.TempJob);
            try
            {
                MeanSquaredErrorJob job = new MeanSquaredErrorJob
                {
                    Left = leftNative,
                    Right = rightNative,
                    Result = result
                };
                job.Schedule(left.Length, 64).Complete();
                float sum = 0f;
                for (int i = 0; i < result.Length; i++) sum += result[i];
                return sum / result.Length;
            }
            finally
            {
                if (leftNative.IsCreated) leftNative.Dispose();
                if (rightNative.IsCreated) rightNative.Dispose();
                if (result.IsCreated) result.Dispose();
            }
        }

        [BurstCompile]
        private struct MeanSquaredErrorJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Left;
            [ReadOnly] public NativeArray<float> Right;
            [WriteOnly] public NativeArray<float> Result;

            public void Execute(int index)
            {
                float difference = Left[index] - Right[index];
                Result[index] = difference * difference;
            }
        }
    }
}
