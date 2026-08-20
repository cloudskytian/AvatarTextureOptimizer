// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using AvatarTextureOptimizer.Editor.Burst;
using AvatarTextureOptimizer.Editor.Texture;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Core
{
    /// <summary>
    /// Compute dispatcher: picks CPU / Burst / GPU paths for the hot resampling operations.
    /// The CPU reference implementation (ATOResampler) is the correctness baseline; Burst
    /// and GPU are drop-in accelerators selected at runtime when available.
    ///
    /// 计算调度器：为热路径重采样选择 CPU / Burst / GPU 路径。CPU 参考实现（ATOResampler）
    /// 是正确性基准；Burst 与 GPU 在可用时作为即插即用的加速路径。
    /// </summary>
    public static class ATOCompute
    {
        /// <summary>Whether the Burst accelerator is available. Burst 加速是否可用。</summary>
        public static bool BurstAvailable => ATOBurstJobs.Available;

        /// <summary>Whether the GPU accelerator is available. GPU 加速是否可用。</summary>
        public static bool GpuAvailable => SystemInfo.supportsComputeShaders && !Application.isBatchMode;

        /// <summary>
        /// Area-average downsample (linear space, premultiplied alpha). Dispatches to Burst
        /// or CPU. 面积平均下采样（线性空间，预乘 alpha）。调度到 Burst 或 CPU。
        /// </summary>
        public static Color[] Downsample(Color[] src, int srcW, int srcH, int dstW, int dstH, bool premultiply)
        {
            if (BurstAvailable && dstW > 0 && dstH > 0 && srcW > 0 && srcH > 0)
            {
                var packed = ATOBurstJobs.Pack(src);
                var dst = new NativeArray<float>(dstW * dstH * 4, Allocator.TempJob);
                var job = new ATOBurstJobs.AreaDownsampleJob
                {
                    src = packed,
                    dst = dst,
                    srcW = srcW, srcH = srcH, dstW = dstW, dstH = dstH,
                    sx = (float)srcW / dstW, sy = (float)srcH / dstH,
                };
                job.Schedule(dstH, 8).Complete();
                var result = ATOBurstJobs.Unpack(dst, dstW * dstH);
                packed.Dispose();
                dst.Dispose();
                return result;
            }

            return ATOResampler.Downsample(src, srcW, srcH, dstW, dstH, premultiply);
        }

        /// <summary>
        /// Bilinear upsample (linear space, premultiplied alpha). Dispatches to Burst or CPU.
        /// 双线性上采样（线性空间，预乘 alpha）。调度到 Burst 或 CPU。
        /// </summary>
        public static Color[] Upsample(Color[] src, int srcW, int srcH, int dstW, int dstH, bool premultiply)
        {
            if (BurstAvailable && dstW > 0 && dstH > 0 && srcW > 0 && srcH > 0)
            {
                var packed = ATOBurstJobs.Pack(src);
                var dst = new NativeArray<float>(dstW * dstH * 4, Allocator.TempJob);
                var job = new ATOBurstJobs.BilinearUpsampleJob
                {
                    src = packed,
                    dst = dst,
                    srcW = srcW, srcH = srcH, dstW = dstW, dstH = dstH,
                };
                job.Schedule(dstH, 8).Complete();
                var result = ATOBurstJobs.Unpack(dst, dstW * dstH);
                packed.Dispose();
                dst.Dispose();
                return result;
            }

            return ATOResampler.BilinearUpsample(src, srcW, srcH, dstW, dstH, premultiply);
        }
    }
}
