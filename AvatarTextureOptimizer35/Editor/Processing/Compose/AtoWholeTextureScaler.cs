using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Whole-texture scaling (no-atlas mode / whitelisted groups / atlas-skipped textures):
    /// banded two-pass bilinear resize (vertical then horizontal) in linear space with
    /// premultiplied alpha — memory-friendly, Burst-accelerated. Normal maps are decoded,
    /// resampled, renormalized and re-encoded. / 整图缩放（不生成图集模式/白名单组/放弃图集化的贴图）：
    /// 分带两遍双线性重采样（先垂直后水平），线性空间、预乘 alpha —— 内存友好、Burst 加速。
    /// 法线贴图按 解码→重采样→重归一化→编码 处理。
    /// </summary>
    internal static class AtoWholeTextureScaler
    {
        private const int Band = 64;

        /// <summary>
        /// Resize a texture to (dstW, dstH). Returns unpremultiplied RGBA32 pixels. /
        /// 把贴图缩放到 (dstW, dstH)。返回反预乘的 RGBA32 像素。
        /// </summary>
        public static Color32[] Resize(Texture2D texture, bool srgb, bool normal, int dstW, int dstH)
        {
            var srcW = texture.width;
            var srcH = texture.height;
            var raw = AtoTextureIO.GetPixels(texture);

            // Decode normals to vectors if needed. / 需要时解码法线为向量。
            var source = raw;
            if (normal)
            {
                source = new Color32[raw.Length];
                for (var i = 0; i < raw.Length; i++)
                {
                    var x = raw[i].r / 255f * 2f - 1f;
                    var y = raw[i].g / 255f * 2f - 1f;
                    var z2 = Mathf.Max(0f, 1f - x * x - y * y);
                    var z = Mathf.Sqrt(z2);
                    source[i] = new Color32(
                        (byte)Mathf.RoundToInt((x * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((y * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((z * 0.5f + 0.5f) * 255f),
                        raw[i].a);
                }
            }

            // Intermediate: srcW × dstH (vertical pass output). / 中间结果：srcW × dstH（垂直通道输出）。
            var intermediate = new NativeArray<float4>(srcW * dstH, Allocator.Persistent);
            var output = new Color32[dstW * dstH];
            try
            {
                // ---- pass A: vertical resize (source column bands) ----
                for (var x0 = 0; x0 < srcW; x0 += Band)
                {
                    var bw = Mathf.Min(Band, srcW - x0);
                    var band = new NativeArray<float4>(bw * srcH, Allocator.TempJob);
                    try
                    {
                        // load + convert to linear premultiplied. / 读取并转线性预乘。
                        for (var y = 0; y < srcH; y++)
                        {
                            for (var x = 0; x < bw; x++)
                            {
                                var c = source[y * srcW + x0 + x];
                                var f = new float4(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
                                if (srgb && !normal) f = AtoBurstKernels.RgbaToLinear(f);
                                if (!normal) f = new float4(f.x * f.w, f.y * f.w, f.z * f.w, f.w);
                                band[y * bw + x] = f;
                            }
                        }
                        // vertical resize per column. / 逐列垂直缩放。
                        for (var x = 0; x < bw; x++)
                        {
                            for (var dy = 0; dy < dstH; dy++)
                            {
                                var sy = (dy + 0.5f) * srcH / dstH - 0.5f;
                                var y0 = Mathf.Clamp((int)Mathf.Floor(sy), 0, srcH - 1);
                                var y1 = Mathf.Clamp(y0 + 1, 0, srcH - 1);
                                var fy = sy - Mathf.Floor(sy);
                                var p0 = band[y0 * bw + x];
                                var p1 = band[y1 * bw + x];
                                intermediate[dy * srcW + x0 + x] = math.lerp(p0, p1, fy);
                            }
                        }
                    }
                    finally
                    {
                        band.Dispose();
                    }
                }

                // ---- pass B: horizontal resize (intermediate row bands) ----
                for (var y0 = 0; y0 < dstH; y0 += Band)
                {
                    var bh = Mathf.Min(Band, dstH - y0);
                    var band = new NativeArray<float4>(srcW * bh, Allocator.TempJob);
                    try
                    {
                        for (var y = 0; y < bh; y++)
                        {
                            for (var x = 0; x < srcW; x++)
                            {
                                band[y * srcW + x] = intermediate[(y0 + y) * srcW + x];
                            }
                        }
                        for (var y = 0; y < bh; y++)
                        {
                            for (var dx = 0; dx < dstW; dx++)
                            {
                                var sx = (dx + 0.5f) * srcW / dstW - 0.5f;
                                var x0i = Mathf.Clamp((int)Mathf.Floor(sx), 0, srcW - 1);
                                var x1i = Mathf.Clamp(x0i + 1, 0, srcW - 1);
                                var fx = sx - Mathf.Floor(sx);
                                var p0 = band[y * srcW + x0i];
                                var p1 = band[y * srcW + x1i];
                                var v = math.lerp(p0, p1, fx);

                                var idx = (y0 + y) * dstW + dx;
                                if (normal)
                                {
                                    var vec = new float3(v.x, v.y, v.z);
                                    var len = math.length(vec);
                                    if (len > 1e-6f) vec /= len;
                                    output[idx] = new Color32(
                                        (byte)Mathf.RoundToInt((vec.x * 0.5f + 0.5f) * 255f),
                                        (byte)Mathf.RoundToInt((vec.y * 0.5f + 0.5f) * 255f),
                                        255, 255);
                                }
                                else
                                {
                                    var a = v.w;
                                    if (a > 1e-4f)
                                    {
                                        v = new float4(v.x / a, v.y / a, v.z / a, a);
                                        if (srgb)
                                        {
                                            v = new float4(
                                                AtoBurstKernels.LinearToSrgb(math.clamp(v.x, 0f, 1f)),
                                                AtoBurstKernels.LinearToSrgb(math.clamp(v.y, 0f, 1f)),
                                                AtoBurstKernels.LinearToSrgb(math.clamp(v.z, 0f, 1f)),
                                                v.w);
                                        }
                                    }
                                    else
                                    {
                                        v = float4.zero;
                                    }
                                    output[idx] = new Color32(
                                        (byte)Mathf.RoundToInt(math.clamp(v.x, 0f, 1f) * 255f),
                                        (byte)Mathf.RoundToInt(math.clamp(v.y, 0f, 1f) * 255f),
                                        (byte)Mathf.RoundToInt(math.clamp(v.z, 0f, 1f) * 255f),
                                        (byte)Mathf.RoundToInt(math.clamp(v.w, 0f, 1f) * 255f));
                                }
                            }
                        }
                    }
                    finally
                    {
                        band.Dispose();
                    }
                }

                return output;
            }
            finally
            {
                intermediate.Dispose();
            }
        }
    }
}
