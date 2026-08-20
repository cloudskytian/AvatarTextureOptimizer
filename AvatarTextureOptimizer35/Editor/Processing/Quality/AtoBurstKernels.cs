using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Burst-compiled kernels for quality evaluation: linear-space conversion, premultiplied
    /// alpha bilinear resampling, Gaussian blur, MS-SSIM/SSIM, ΔE00 (CIEDE2000), alpha metrics,
    /// normal-map angle metrics, grayscale RMSE. / 质量评估的 Burst 核：线性空间转换、预乘 alpha
    /// 双线性重采样、高斯模糊、MS-SSIM/SSIM、ΔE00（CIEDE2000）、alpha 指标、法线角度指标、灰度 RMSE。
    /// </summary>
    internal static class AtoBurstKernels
    {
        // ---------------------------------------------------------------
        // color space & premultiplication
        // ---------------------------------------------------------------

        /// <summary>sRGB → linear (approximation-free, standard transfer). / sRGB → 线性（标准转换）。</summary>
        [BurstCompile]
        public static float SrgbToLinear(float c) =>
            c <= 0.04045f ? c / 12.92f : math.pow((c + 0.055f) / 1.055f, 2.4f);

        /// <summary>Linear → sRGB. / 线性 → sRGB。</summary>
        [BurstCompile]
        public static float LinearToSrgb(float c) =>
            c <= 0.0031308f ? c * 12.92f : 1.055f * math.pow(c, 1f / 2.4f) - 0.055f;

        [BurstCompile]
        public static float4 RgbaToLinear(float4 c)
        {
            return new float4(SrgbToLinear(c.x), SrgbToLinear(c.y), SrgbToLinear(c.z), c.w);
        }

        /// <summary>
        /// Convert raw RGBA32 pixels to linear premultiplied float4. Pixels outside the mask are
        /// set to (0,0,0,0) — safe for premultiplied resampling. / 把原始 RGBA32 像素转为线性预乘
        /// float4。掩码外像素置 (0,0,0,0) —— 预乘重采样安全。
        /// </summary>
        [BurstCompile]
        public struct ConvertToLinearJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Color32> Source;
            [ReadOnly] public NativeArray<byte> Mask;
            [ReadOnly] public bool Srgb;
            [ReadOnly] public bool Premultiply;
            [WriteOnly] public NativeArray<float4> Output;

            public void Execute(int i)
            {
                if (Mask[i] == 0)
                {
                    Output[i] = float4.zero;
                    return;
                }
                var c = Source[i];
                var f = new float4(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
                if (Srgb) f = RgbaToLinear(f);
                if (Premultiply)
                {
                    // premultiply. / 预乘。
                    f = new float4(f.x * f.w, f.y * f.w, f.z * f.w, f.w);
                }
                Output[i] = f;
            }
        }

        // ---------------------------------------------------------------
        // bilinear premultiplied resampling (single image resize)
        // ---------------------------------------------------------------

        /// <summary>
        /// Bilinear resize of a premultiplied float4 image (edge-clamped sampling). /
        /// 预乘 float4 图像的双线性缩放（边缘钳制采样）。
        /// </summary>
        [BurstCompile]
        public struct BilinearResizeJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float4> Source;
            [ReadOnly] public int SourceWidth;
            [ReadOnly] public int SourceHeight;
            [ReadOnly] public int DestWidth;
            [ReadOnly] public int DestHeight;
            [WriteOnly] public NativeArray<float4> Output;

            public void Execute(int index)
            {
                var dy = index / DestWidth;
                var dx = index - dy * DestWidth;

                // Map destination pixel center to source space. / 目标像素中心映射到源空间。
                var sx = (dx + 0.5f) * SourceWidth / DestWidth - 0.5f;
                var sy = (dy + 0.5f) * SourceHeight / DestHeight - 0.5f;

                var x0 = math.clamp((int)math.floor(sx), 0, SourceWidth - 1);
                var y0 = math.clamp((int)math.floor(sy), 0, SourceHeight - 1);
                var x1 = math.clamp(x0 + 1, 0, SourceWidth - 1);
                var y1 = math.clamp(y0 + 1, 0, SourceHeight - 1);
                var fx = sx - math.floor(sx);
                var fy = sy - math.floor(sy);

                var p00 = Source[y0 * SourceWidth + x0];
                var p10 = Source[y0 * SourceWidth + x1];
                var p01 = Source[y1 * SourceWidth + x0];
                var p11 = Source[y1 * SourceWidth + x1];

                Output[index] = math.lerp(math.lerp(p00, p10, fx), math.lerp(p01, p11, fx), fy);
            }
        }

        /// <summary>
        /// Separable Gaussian blur (edge-clamped), single float channel. / 单通道可分离高斯模糊（边缘钳制）。
        /// </summary>
        [BurstCompile]
        public struct GaussianBlurJob : IJob
        {
            [ReadOnly] public NativeArray<float> Source;
            public NativeArray<float> Output;
            [ReadOnly] public int Width;
            [ReadOnly] public int Height;
            [ReadOnly] public int Radius;
            [ReadOnly] public float Sigma;
            public NativeArray<float> Kernel; // size 2*Radius+1, allocated by caller. / 由调用方分配，尺寸 2*Radius+1。

            public void Execute()
            {
                // build kernel. / 构建核。
                var sum = 0f;
                for (var i = -Radius; i <= Radius; i++)
                {
                    var v = math.exp(-(i * i) / (2f * Sigma * Sigma));
                    Kernel[i + Radius] = v;
                    sum += v;
                }
                for (var i = 0; i <= 2 * Radius; i++) Kernel[i] /= sum;

                var temp = new NativeArray<float>(Width * Height, Allocator.Temp);
                try
                {
                    // horizontal pass. / 水平方向。
                    for (var y = 0; y < Height; y++)
                    {
                        for (var x = 0; x < Width; x++)
                        {
                            var acc = 0f;
                            for (var k = -Radius; k <= Radius; k++)
                            {
                                var xx = math.clamp(x + k, 0, Width - 1);
                                acc += Source[y * Width + xx] * Kernel[k + Radius];
                            }
                            temp[y * Width + x] = acc;
                        }
                    }
                    // vertical pass. / 垂直方向。
                    for (var y = 0; y < Height; y++)
                    {
                        for (var x = 0; x < Width; x++)
                        {
                            var acc = 0f;
                            for (var k = -Radius; k <= Radius; k++)
                            {
                                var yy = math.clamp(y + k, 0, Height - 1);
                                acc += temp[yy * Width + x] * Kernel[k + Radius];
                            }
                            Output[y * Width + x] = acc;
                        }
                    }
                }
                finally
                {
                    temp.Dispose();
                }
            }
        }

        // ---------------------------------------------------------------
        // SSIM / MS-SSIM (Wang et al. 2003)
        // ---------------------------------------------------------------

        /// <summary>
        /// Compute the per-pixel SSIM map of two images with a Gaussian window. /
        /// 计算两幅图像的逐像素 SSIM 图（高斯窗口）。
        /// </summary>
        [BurstCompile]
        public struct SsimMapJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> MuX;     // blurred x. / x 的模糊。
            [ReadOnly] public NativeArray<float> MuY;
            [ReadOnly] public NativeArray<float> MuXX;    // blurred x*x.
            [ReadOnly] public NativeArray<float> MuYY;
            [ReadOnly] public NativeArray<float> MuXY;
            [ReadOnly] public NativeArray<byte> Mask;
            [WriteOnly] public NativeArray<float> Map;    // 0 where masked out. / 掩码外为 0。

            public void Execute(int i)
            {
                if (Mask[i] == 0)
                {
                    Map[i] = 0f;
                    return;
                }
                var c1 = 0.01f * 0.01f;
                var c2 = 0.03f * 0.03f;
                var sigmaX2 = MuXX[i] - MuX[i] * MuX[i];
                var sigmaY2 = MuYY[i] - MuY[i] * MuY[i];
                var sigmaXY = MuXY[i] - MuX[i] * MuY[i];
                var numerator = (2f * MuX[i] * MuY[i] + c1) * (2f * sigmaXY + c2);
                var denominator = (MuX[i] * MuX[i] + MuY[i] * MuY[i] + c1) * (sigmaX2 + sigmaY2 + c2);
                Map[i] = denominator > 1e-12f ? numerator / denominator : 1f;
            }
        }

        /// <summary>
        /// Downsample a float image 2× (box average). / 2× 盒式降采样 float 图像。
        /// </summary>
        [BurstCompile]
        public struct Downsample2xJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Source;
            [ReadOnly] public int Width;
            [ReadOnly] public int Height;
            [WriteOnly] public NativeArray<float> Output;
            [ReadOnly] public int OutWidth;

            public void Execute(int index)
            {
                var y = index / OutWidth;
                var x = index - y * OutWidth;
                var x0 = x * 2;
                var y0 = y * 2;
                var v = (Source[y0 * Width + x0] +
                         Source[y0 * Width + math.min(x0 + 1, Width - 1)] +
                         Source[math.min(y0 + 1, Height - 1) * Width + x0] +
                         Source[math.min(y0 + 1, Height - 1) * Width + math.min(x0 + 1, Width - 1)]) * 0.25f;
                Output[index] = v;
            }
        }

        /// <summary>
        /// Downsample an occupancy mask 2× (any set pixel → set). / 2× 降采样占用掩码（任一置位则置位）。
        /// </summary>
        [BurstCompile]
        public struct DownsampleMask2xJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> Source;
            [ReadOnly] public int Width;
            [ReadOnly] public int Height;
            [WriteOnly] public NativeArray<byte> Output;
            [ReadOnly] public int OutWidth;

            public void Execute(int index)
            {
                var y = index / OutWidth;
                var x = index - y * OutWidth;
                var x0 = x * 2;
                var y0 = y * 2;
                var v = Source[y0 * Width + x0] |
                        Source[y0 * Width + math.min(x0 + 1, Width - 1)] |
                        Source[math.min(y0 + 1, Height - 1) * Width + x0] |
                        Source[math.min(y0 + 1, Height - 1) * Width + math.min(x0 + 1, Width - 1)];
                Output[index] = v;
            }
        }

        // ---------------------------------------------------------------
        // ΔE00 (CIEDE2000) per pixel
        // ---------------------------------------------------------------

        /// <summary>
        /// Compute the mean ΔE00 over masked pixels of two linear-RGB float4 images. /
        /// 计算两幅线性 RGB float4 图像在掩码像素上的 ΔE00 均值。
        /// </summary>
        [BurstCompile]
        public struct DeltaE00Job : IJob
        {
            [ReadOnly] public NativeArray<float4> Ref;
            [ReadOnly] public NativeArray<float4> Candidate;
            [ReadOnly] public NativeArray<byte> Mask;
            public NativeArray<float> Result; // [0] = mean, [1] = count. / [0]=均值，[1]=数量。

            public void Execute()
            {
                var sum = 0.0;
                var count = 0;
                for (var i = 0; i < Ref.Length; i++)
                {
                    if (Mask[i] == 0) continue;
                    var de = Ciede2000(ToLab(Ref[i]), ToLab(Candidate[i]));
                    sum += de;
                    count++;
                }
                Result[0] = count > 0 ? (float)(sum / count) : 0f;
                Result[1] = count;
            }
        }

        [BurstCompile]
        private static float3 ToLab(float4 linearRgb)
        {
            // linear sRGB (D65) → XYZ → Lab. / 线性 sRGB（D65）→ XYZ → Lab。
            var x = 0.4124564f * linearRgb.x + 0.3575761f * linearRgb.y + 0.1804375f * linearRgb.z;
            var y = 0.2126729f * linearRgb.x + 0.7151522f * linearRgb.y + 0.0721750f * linearRgb.z;
            var z = 0.0193339f * linearRgb.x + 0.1191920f * linearRgb.y + 0.9503041f * linearRgb.z;

            var xn = 0.95047f;
            var yn = 1.0f;
            var zn = 1.08883f;

            float F(float t) => t > 0.008856f ? math.cbrt(t) : (7.787f * t + 16f / 116f);

            var fx = F(x / xn);
            var fy = F(y / yn);
            var fz = F(z / zn);
            return new float3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
        }

        [BurstCompile]
        private static float Ciede2000(float3 lab1, float3 lab2)
        {
            // CIEDE2000 (Sharma, Wu, Dalal 2005). / CIEDE2000（Sharma, Wu, Dalal 2005）。
            var l1 = lab1.x;
            var a1 = lab1.y;
            var b1 = lab1.z;
            var l2 = lab2.x;
            var a2 = lab2.y;
            var b2 = lab2.z;

            var c1 = math.sqrt(a1 * a1 + b1 * b1);
            var c2 = math.sqrt(a2 * a2 + b2 * b2);
            var cMean = (c1 + c2) * 0.5f;
            var cMean7 = math.pow(cMean, 7f);
            var g = 0.5f * (1f - math.sqrt(cMean7 / (cMean7 + math.pow(25f, 7f))));
            var a1p = (1f + g) * a1;
            var a2p = (1f + g) * a2;
            var c1p = math.sqrt(a1p * a1p + b1 * b1);
            var c2p = math.sqrt(a2p * a2p + b2 * b2);

            float Hue(float ap, float b)
            {
                if (ap == 0f && b == 0f) return 0f;
                var h = math.atan2(b, ap) * 57.2957795f;
                return h < 0f ? h + 360f : h;
            }

            var h1p = Hue(a1p, b1);
            var h2p = Hue(a2p, b2);

            var dLp = l2 - l1;
            var dCp = c2p - c1p;

            float Dhp;
            if (c1p * c2p == 0f) Dhp = 0f;
            else if (math.abs(h2p - h1p) <= 180f) Dhp = h2p - h1p;
            else if (h2p - h1p > 180f) Dhp = h2p - h1p - 360f;
            else Dhp = h2p - h1p + 360f;

            var dHp = 2f * math.sqrt(c1p * c2p) * math.sin(Dhp * 0.5f * 0.0174532925f);

            var lMean = (l1 + l2) * 0.5f;
            var cpMean = (c1p + c2p) * 0.5f;
            float hpMean;
            if (c1p * c2p == 0f)
            {
                hpMean = h1p + h2p;
            }
            else if (math.abs(h1p - h2p) <= 180f)
            {
                hpMean = (h1p + h2p) * 0.5f;
            }
            else if (h1p + h2p < 360f)
            {
                hpMean = (h1p + h2p + 360f) * 0.5f;
            }
            else
            {
                hpMean = (h1p + h2p - 360f) * 0.5f;
            }

            var t = 1f - 0.17f * math.cos((hpMean - 30f) * 0.0174532925f) +
                    0.24f * math.cos((2f * hpMean) * 0.0174532925f) +
                    0.32f * math.cos((3f * hpMean + 6f) * 0.0174532925f) -
                    0.20f * math.cos((4f * hpMean - 63f) * 0.0174532925f);

            var dTheta = 30f * math.exp(-((hpMean - 275f) / 25f) * ((hpMean - 275f) / 25f));
            var rc = 2f * math.sqrt(math.pow(cpMean, 7f) / (math.pow(cpMean, 7f) + math.pow(25f, 7f)));
            var sl = 1f + (0.015f * (lMean - 50f) * (lMean - 50f)) / math.sqrt(20f + (lMean - 50f) * (lMean - 50f));
            var sc = 1f + 0.045f * cpMean;
            var sh = 1f + 0.015f * cpMean * t;
            var rt = -math.sin(2f * dTheta * 0.0174532925f) * rc;

            var dL = dLp / sl;
            var dC = dCp / sc;
            var dH = dHp / sh;
            return math.sqrt(dL * dL + dC * dC + dH * dH + rt * dC * dH);
        }

        // ---------------------------------------------------------------
        // alpha metrics
        // ---------------------------------------------------------------

        /// <summary>
        /// Cutout alpha: IoU of the clipped contours at a given cutoff. / Cutout alpha：指定阈值裁剪轮廓的 IoU。
        /// </summary>
        [BurstCompile]
        public struct CutoutIoUJob : IJob
        {
            [ReadOnly] public NativeArray<float4> Ref;
            [ReadOnly] public NativeArray<float4> Candidate;
            [ReadOnly] public NativeArray<byte> Mask;
            [ReadOnly] public float Cutoff;
            public NativeArray<float> Result; // [0] = IoU, [1] = ref pixel count. / [0]=IoU，[1]=参考像素数。

            public void Execute()
            {
                float intersect = 0;
                float union = 0;
                var refCount = 0;
                for (var i = 0; i < Ref.Length; i++)
                {
                    if (Mask[i] == 0) continue;
                    var r = Ref[i].w >= Cutoff;
                    var c = Candidate[i].w >= Cutoff;
                    if (r) refCount++;
                    if (r && c) intersect++;
                    if (r || c) union++;
                }
                Result[0] = union > 0 ? intersect / union : 1f;
                Result[1] = refCount;
            }
        }

        /// <summary>
        /// Blend alpha: linear RMSE over masked pixels. / Blend alpha：掩码像素上的线性 RMSE。
        /// </summary>
        [BurstCompile]
        public struct AlphaRmseJob : IJob
        {
            [ReadOnly] public NativeArray<float4> Ref;
            [ReadOnly] public NativeArray<float4> Candidate;
            [ReadOnly] public NativeArray<byte> Mask;
            public NativeArray<float> Result;

            public void Execute()
            {
                var sum = 0.0;
                var count = 0;
                for (var i = 0; i < Ref.Length; i++)
                {
                    if (Mask[i] == 0) continue;
                    var d = Candidate[i].w - Ref[i].w;
                    sum += d * d;
                    count++;
                }
                Result[0] = count > 0 ? (float)math.sqrt(sum / count) : 0f;
                Result[1] = count;
            }
        }

        // ---------------------------------------------------------------
        // normal map angle metrics
        // ---------------------------------------------------------------

        /// <summary>
        /// Normal map: mean & p95 angle error (degrees) over masked pixels. Inputs are decoded
        /// tangent-space vectors (normalized). / 法线贴图：掩码像素上的角度误差（度）均值与 p95。
        /// 输入为解码后的切线空间向量（已归一化）。
        /// </summary>
        [BurstCompile]
        public struct NormalAngleJob : IJob
        {
            [ReadOnly] public NativeArray<float4> Ref;
            [ReadOnly] public NativeArray<float4> Candidate;
            [ReadOnly] public NativeArray<byte> Mask;
            public NativeArray<float> Result; // [0]=mean(deg), [1]=p95(deg), [2]=count. / [0]=均值，[1]=p95，[2]=数量。

            public void Execute()
            {
                var count = 0;
                for (var i = 0; i < Ref.Length; i++)
                {
                    if (Mask[i] != 0) count++;
                }

                if (count == 0)
                {
                    Result[0] = 0f;
                    Result[1] = 0f;
                    Result[2] = 0;
                    return;
                }

                var angles = new NativeArray<float>(count, Allocator.Temp);
                try
                {
                    var idx = 0;
                    var sum = 0.0;
                    for (var i = 0; i < Ref.Length; i++)
                    {
                        if (Mask[i] == 0) continue;
                        var a = math.normalize(new float3(Ref[i].x, Ref[i].y, Ref[i].z));
                        var b = math.normalize(new float3(Candidate[i].x, Candidate[i].y, Candidate[i].z));
                        var dot = math.clamp(math.dot(a, b), -1f, 1f);
                        var angle = math.acos(dot) * 57.2957795f;
                        angles[idx++] = angle;
                        sum += angle;
                    }
                    angles.Sort();
                    Result[0] = (float)(sum / count);
                    // p95: value at ceil(0.95*count)-1 (nearest rank). / p95：按最近秩取第 95 百分位。
                    var rank = math.max(0, (int)math.ceil(0.95f * count) - 1);
                    Result[1] = angles[math.min(rank, count - 1)];
                    Result[2] = count;
                }
                finally
                {
                    angles.Dispose();
                }
            }
        }

        /// <summary>
        /// Decode a normal map (DXT5nm-style or plain RGB) to normalized tangent vectors. /
        /// 解码法线贴图（DXT5nm 或普通 RGB）为归一化切线向量。
        /// </summary>
        [BurstCompile]
        public struct NormalDecodeJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float4> Source; // linear premultiplied float4. / 线性预乘 float4。
            [ReadOnly] public NativeArray<byte> Mask;
            [WriteOnly] public NativeArray<float4> Output; // xyz = vector, w = alpha kept. / xyz=向量，w=保留 alpha。

            public void Execute(int i)
            {
                if (Mask[i] == 0)
                {
                    Output[i] = float4.zero;
                    return;
                }
                var c = Source[i];
                var x = c.x * 2f - 1f;
                var y = c.y * 2f - 1f;
                var z2 = math.max(0f, 1f - x * x - y * y);
                Output[i] = new float4(x, y, math.sqrt(z2), c.w);
            }
        }

        /// <summary>
        /// Grayscale linear RMSE on used channels (worst channel wins). / 灰度：使用通道的线性 RMSE（取最差通道）。
        /// </summary>
        /// <summary>
        /// GPU/CPU pull-push (infinite dilation) via jump flooding: fills empty atlas pixels by
        /// dilating edge colors in O(n log n). Background alpha stays 0 (premultiplied-safe;
        /// bleeding is a known trade-off). / pull-push（无限外扩）跳跃洪泛实现：O(n log n) 用岛边缘颜色
        /// 外扩填充图集空白。背景 alpha 保持 0（预乘安全；渗色已知可接受）。
        /// </summary>
        [BurstCompile]
        public static void Dilate(NativeArray<Color32> pixels, NativeArray<Color32> scratch, int width, int height)
        {
            var maxStep = 1;
            while (maxStep * 2 < Mathf.Max(width, height)) maxStep *= 2;

            for (var step = maxStep; step >= 1; step /= 2)
            {
                // copy current state. / 复制当前状态。
                scratch.CopyFrom(pixels);
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var i = y * width + x;
                        if (pixels[i].a != 0) continue;
                        var r = 0;
                        var g = 0;
                        var b = 0;
                        var count = 0;
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                var nx = x + dx * step;
                                var ny = y + dy * step;
                                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                                var p = scratch[ny * width + nx];
                                if (p.a != 0)
                                {
                                    r += p.r;
                                    g += p.g;
                                    b += p.b;
                                    count++;
                                }
                            }
                        }
                        if (count > 0)
                        {
                            // Keep alpha 0 in the background (transparent atlases); the RGB carries
                            // the dilated edge color (bleed trade-off). / 背景 alpha 保持 0（透明图集）；
                            // RGB 携带外扩边缘色（渗色代价）。
                            pixels[i] = new Color32((byte)(r / count), (byte)(g / count), (byte)(b / count), 0);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Same dilation for OPAQUE atlases: filled pixels get alpha 255. / 不透明图集的膨胀：填充像素 alpha=255。
        /// </summary>
        [BurstCompile]
        public static void DilateOpaque(NativeArray<Color32> pixels, NativeArray<Color32> scratch, int width, int height)
        {
            Dilate(pixels, scratch, width, height);
            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                if (p.a == 0) pixels[i] = new Color32(p.r, p.g, p.b, 255);
            }
        }

        [BurstCompile]
        public struct GrayRmseJob : IJob
        {
            [ReadOnly] public NativeArray<float4> Ref;
            [ReadOnly] public NativeArray<float4> Candidate;
            [ReadOnly] public NativeArray<byte> Mask;
            [ReadOnly] public int UsedChannels; // bitmask R|G|B|A. / R|G|B|A 位掩码。
            public NativeArray<float> Result; // [0]=worst RMSE, [1]=count. / [0]=最差 RMSE，[1]=数量。

            public void Execute()
            {
                double r = 0, g = 0, b = 0, a = 0;
                var count = 0;
                for (var i = 0; i < Ref.Length; i++)
                {
                    if (Mask[i] == 0) continue;
                    var dx = Candidate[i] - Ref[i];
                    if ((UsedChannels & 1) != 0) r += dx.x * dx.x;
                    if ((UsedChannels & 2) != 0) g += dx.y * dx.y;
                    if ((UsedChannels & 4) != 0) b += dx.z * dx.z;
                    if ((UsedChannels & 8) != 0) a += dx.w * dx.w;
                    count++;
                }
                var worst = 0.0;
                if ((UsedChannels & 1) != 0) worst = math.max(worst, r);
                if ((UsedChannels & 2) != 0) worst = math.max(worst, g);
                if ((UsedChannels & 4) != 0) worst = math.max(worst, b);
                if ((UsedChannels & 8) != 0) worst = math.max(worst, a);
                Result[0] = count > 0 ? (float)math.sqrt(worst / count) : 0f;
                Result[1] = count;
            }
        }
    }
}
