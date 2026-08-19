using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Burst-parallel mean CIEDE2000 over packed linear RGB8-ish floats (rgba).
    /// Burst 并行计算线性 RGB 的平均 CIEDE2000。
    /// </summary>
    [BurstCompile]
    public struct Ciede2000Job : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float4> A;
        [ReadOnly] public NativeArray<float4> B;
        public NativeArray<float> Partial;

        public void Execute(int index)
        {
            var a = A[index];
            var b = B[index];
            ColorScience.RgbToLab(a.x, a.y, a.z, out var L1, out var a1, out var b1);
            ColorScience.RgbToLab(b.x, b.y, b.z, out var L2, out var a2, out var b2);
            Partial[index] = ColorScience.Ciede2000(L1, a1, b1, L2, a2, b2);
        }
    }
}
