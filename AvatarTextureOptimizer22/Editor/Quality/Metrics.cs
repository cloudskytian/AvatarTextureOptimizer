// AvatarTextureOptimizer
// File: Editor/Quality/Metrics.cs
//
// Alpha, normal-map and grayscale metrics (Burst jobs).
//   - AlphaBlendRMSE: linear RMSE of alpha (Blend mode)
//   - CutoutIoU: contour IoU after thresholding alpha at the cutoff
//   - NormalAngularError: per-pixel angular error; returns mean + p95
//   - GrayChannelRMSE: per-channel linear RMSE on used channels (worst)
//
// alpha、法线贴图与灰度指标（Burst 任务）。
//   - AlphaBlendRMSE：alpha 的线性 RMSE（Blend 模式）
//   - CutoutIoU：按 cutoff 阈值化 alpha 后的轮廓 IoU
//   - NormalAngularError：逐像素角度误差；返回均值 + p95
//   - GrayChannelRMSE：被使用通道上的逐通道线性 RMSE（取最差）

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.quality
{
    public static class Metrics
    {
        // ---- Alpha: Blend RMSE / Blend 模式 alpha RMSE ----

        [BurstCompile]
        public struct AlphaRMSEJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Color> A;
            [ReadOnly] public NativeArray<Color> B;
            [WriteOnly] public NativeArray<float> Result;

            public void Execute(int i)
            {
                float d = A[i].a - B[i].a;
                Result[i] = d * d;
            }
        }

        public static float AlphaBlendRMSE(Color[] a, Color[] b)
        {
            int n = Mathf.Min(a.Length, b.Length);
            if (n == 0) return 0f;
            using var na = new NativeArray<Color>(n, Allocator.TempJob);
            using var nb = new NativeArray<Color>(n, Allocator.TempJob);
            using var res = new NativeArray<float>(n, Allocator.TempJob);
            NativeArray<Color>.Copy(a, na, n);
            NativeArray<Color>.Copy(b, nb, n);
            new AlphaRMSEJob { A = na, B = nb, Result = res }.Schedule(n, 64).Complete();
            double sum = 0;
            for (int i = 0; i < n; i++) sum += res[i];
            return (float)Math.Sqrt(sum / n);
        }

        // ---- Alpha: Cutout contour IoU / Cutout 轮廓 IoU ----

        [BurstCompile]
        public struct CutoutIoUJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Color> A;
            [ReadOnly] public NativeArray<Color> B;
            public float Cutoff;

            public void Execute(int i)
            {
                // result: 1 = both inside, 2 = both outside, 3 = A only, 4 = B only
                // 结果：1 = 都在内，2 = 都在外，3 = 仅 A，4 = 仅 B
                bool ia = A[i].a > Cutoff;
                bool ib = B[i].a > Cutoff;
                if (ia && ib) Result[i] = 1;
                else if (!ia && !ib) Result[i] = 2;
                else if (ia) Result[i] = 3;
                else Result[i] = 4;
            }

            [WriteOnly] public NativeArray<float> Result;
        }

        public static float CutoutIoU(Color[] a, Color[] b, float cutoff)
        {
            int n = Mathf.Min(a.Length, b.Length);
            if (n == 0) return 1f;
            using var na = new NativeArray<Color>(n, Allocator.TempJob);
            using var nb = new NativeArray<Color>(n, Allocator.TempJob);
            using var res = new NativeArray<float>(n, Allocator.TempJob);
            NativeArray<Color>.Copy(a, na, n);
            NativeArray<Color>.Copy(b, nb, n);
            new CutoutIoUJob { A = na, B = nb, Cutoff = cutoff, Result = res }.Schedule(n, 64).Complete();
            long both = 0, either = 0;
            for (int i = 0; i < n; i++)
            {
                if (res[i] <= 2) both++;
                if (res[i] <= 4) either++;
            }
            return either == 0 ? 1f : (float)((double)both / either);
        }

        // ---- Normal map angular error (radians) + p95 ----
        // ---- 法线贴图角度误差（弧度）+ p95 ----

        [BurstCompile]
        public struct NormalAngleJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> A;
            [ReadOnly] public NativeArray<Vector3> B;
            [WriteOnly] public NativeArray<float> Result;

            public void Execute(int i)
            {
                // Inputs are ALREADY decoded to [-1,1] normal vectors by the
                // caller; do not decode again. / 输入已被调用方解码为 [-1,1]
                // 法线向量；此处不得再次解码。
                var x = A[i];
                var y = B[i];
                float lx = Mathf.Max(x.magnitude, 1e-6f);
                float ly = Mathf.Max(y.magnitude, 1e-6f);
                float cosA = Mathf.Clamp(Vector3.Dot(x / lx, y / ly), -1f, 1f);
                Result[i] = Mathf.Acos(cosA) * Mathf.Rad2Deg;
            }
        }

        public static (float Mean, float P95) NormalAngularError(Color[] a, Color[] b)
        {
            int n = Mathf.Min(a.Length, b.Length);
            if (n == 0) return (0f, 0f);
            using var na = new NativeArray<Vector3>(n, Allocator.TempJob);
            using var nb = new NativeArray<Vector3>(n, Allocator.TempJob);
            using var res = new NativeArray<float>(n, Allocator.TempJob);
            for (int i = 0; i < n; i++)
            {
                na[i] = new Vector3(a[i].r * 2f - 1f, a[i].g * 2f - 1f, a[i].b * 2f - 1f);
                nb[i] = new Vector3(b[i].r * 2f - 1f, b[i].g * 2f - 1f, b[i].b * 2f - 1f);
            }
            new NormalAngleJob { A = na, B = nb, Result = res }.Schedule(n, 64).Complete();

            var sorted = new float[n];
            double sum = 0;
            for (int i = 0; i < n; i++) { sorted[i] = res[i]; sum += res[i]; }
            Array.Sort(sorted);
            float p95 = sorted[Mathf.Clamp((int)(n * 0.95f), 0, n - 1)];
            return ((float)(sum / n), p95);
        }

        // ---- Grayscale: worst per-channel linear RMSE ----
        // ---- 灰度：逐通道线性 RMSE 的最差者 ----

        [BurstCompile]
        public struct GrayChannelRMSEJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Color> A;
            [ReadOnly] public NativeArray<Color> B;
            [WriteOnly] public NativeArray<Vector4> Result; // (dr^2, dg^2, db^2, da^2)

            public void Execute(int i)
            {
                var d = A[i] - B[i];
                Result[i] = new Vector4(d.r * d.r, d.g * d.g, d.b * d.b, d.a * d.a);
            }
        }

        /// <summary>
        /// Worst-channel linear RMSE. When only some channels are used (mask
        /// semantics), pass `usedChannels` to restrict evaluation.
        /// 最差通道线性 RMSE。仅部分通道被使用时传入 usedChannels 限制评估。
        /// </summary>
        public static float GrayChannelRMSE(Color[] a, Color[] b, bool[] usedChannels = null)
        {
            int n = Mathf.Min(a.Length, b.Length);
            if (n == 0) return 0f;
            using var na = new NativeArray<Color>(n, Allocator.TempJob);
            using var nb = new NativeArray<Color>(n, Allocator.TempJob);
            using var res = new NativeArray<Vector4>(n, Allocator.TempJob);
            NativeArray<Color>.Copy(a, na, n);
            NativeArray<Color>.Copy(b, nb, n);
            new GrayChannelRMSEJob { A = na, B = nb, Result = res }.Schedule(n, 64).Complete();

            double[] sums = { 0, 0, 0, 0 };
            for (int i = 0; i < n; i++)
            {
                var v = res[i];
                sums[0] += v.x; sums[1] += v.y; sums[2] += v.z; sums[3] += v.w;
            }
            float worst = 0f;
            for (int c = 0; c < 4; c++)
            {
                if (usedChannels != null && c < usedChannels.Length && !usedChannels[c]) continue;
                float rmse = (float)Math.Sqrt(sums[c] / n);
                if (rmse > worst) worst = rmse;
            }
            return worst;
        }
    }
}
