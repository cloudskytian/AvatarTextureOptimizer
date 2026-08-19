// Avatar Texture Optimizer / 头像贴图优化器
// Burst-compiled metric kernels. All kernels are per-pixel or per-scanline and
// operate on NativeArrays; reductions (sums / histograms) run on the managed
// side over small island-crop arrays.
// Burst 编译的指标内核。全部按像素/扫描线并行，作用于 NativeArray；归约
// （求和/直方图）在托管侧对小岛裁剪数组完成。

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Linear + Lab conversions for one byte. / 单字节的线性化与 Lab 转换。</summary>
    public static class ATOColorMath
    {
        // sRGB -> linear / sRGB 转线性
        public static float SrgbToLinear(float c)
        {
            c = Mathf.Clamp01(c);
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        // display luma (Rec.709 on sRGB bytes) / 显示亮度（sRGB 上的 Rec.709）
        public static float Luma709(byte r, byte g, byte b)
        {
            return (0.2126f * r + 0.7152f * g + 0.0722f * b) / 255f;
        }

        /// <summary>sRGB byte triple -> CIE Lab (D65). / sRGB 字节三元组转 CIE Lab（D65）。</summary>
        public static void RgbToLab(byte rb, byte gb, byte bb, out float L, out float A, out float B)
        {
            float r = SrgbToLinear(rb / 255f);
            float g = SrgbToLinear(gb / 255f);
            float b = SrgbToLinear(bb / 255f);
            // XYZ (D65 sRGB matrix) / XYZ 转换
            float x = r * 0.4124564f + g * 0.3575761f + b * 0.1804375f;
            float y = r * 0.2126729f + g * 0.7151522f + b * 0.0721750f;
            float z = r * 0.0193339f + g * 0.1191920f + b * 0.9503041f;
            x /= 0.95047f; // D65 white / D65 白点
            z /= 1.08883f;
            float fx = LabF(x), fy = LabF(y), fz = LabF(z);
            L = 116f * fy - 16f;
            A = 500f * (fx - fy);
            B = 200f * (fy - fz);
        }

        private static float LabF(float t)
        {
            const float d = 6f / 29f;
            return t > d * d * d ? Mathf.Pow(t, 1f / 3f) : t / (3f * d * d) + 4f / 29f;
        }

        /// <summary>CIEDE2000 distance (Sharma 2005, kL=kC=kH=1). / CIEDE2000 色差。</summary>
        public static float DeltaE2000(float l1, float a1, float b1, float l2, float a2, float b2)
        {
            float c1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float c2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float cm = (c1 + c2) * 0.5f;
            float cm7 = Mathf.Pow(cm, 7f);
            float g = 0.5f * (1f - Mathf.Sqrt(cm7 / (cm7 + 6103515625f))); // 25^7
            float ap1 = a1 * (1f + g);
            float ap2 = a2 * (1f + g);
            float cp1 = Mathf.Sqrt(ap1 * ap1 + b1 * b1);
            float cp2 = Mathf.Sqrt(ap2 * ap2 + b2 * b2);
            float hp1 = Mathf.Atan2(b1, ap1);
            float hp2 = Mathf.Atan2(b2, ap2);
            if (hp1 < 0) hp1 += Mathf.PI * 2f;
            if (hp2 < 0) hp2 += Mathf.PI * 2f;

            float dLp = l2 - l1;
            float dCp = cp2 - cp1;
            float dhp = 0f;
            if (cp1 * cp2 > 1e-6f)
            {
                dhp = hp2 - hp1;
                if (dhp > Mathf.PI) dhp -= Mathf.PI * 2f;
                else if (dhp < -Mathf.PI) dhp += Mathf.PI * 2f;
            }
            float dHp = 2f * Mathf.Sqrt(cp1 * cp2) * Mathf.Sin(dhp * 0.5f);

            float Lp = (l1 + l2) * 0.5f;
            float Cp = (cp1 + cp2) * 0.5f;
            float Hp;
            if (cp1 * cp2 <= 1e-6f) Hp = hp1 + hp2;
            else if (Mathf.Abs(hp1 - hp2) <= Mathf.PI) Hp = (hp1 + hp2) * 0.5f;
            else Hp = (hp1 + hp2 + (hp1 + hp2 < Mathf.PI * 2f ? Mathf.PI * 2f : -Mathf.PI * 2f)) * 0.5f;

            float t = 1f - 0.17f * Mathf.Cos(Hp - Mathf.Deg2Rad * 30f)
                      + 0.24f * Mathf.Cos(2f * Hp)
                      + 0.32f * Mathf.Cos(3f * Hp + Mathf.Deg2Rad * 6f)
                      - 0.20f * Mathf.Cos(4f * Hp - Mathf.Deg2Rad * 63f);
            float dRo = 30f * Mathf.Exp(-Mathf.Pow((Hp - Mathf.Deg2Rad * 275f) / (Mathf.Deg2Rad * 25f), 2f));
            float Cp7 = Mathf.Pow(Cp, 7f);
            float Rc = 2f * Mathf.Sqrt(Cp7 / (Cp7 + 6103515625f));
            float Sl = 1f + 0.015f * (Lp - 50f) * (Lp - 50f) / Mathf.Sqrt(20f + (Lp - 50f) * (Lp - 50f));
            float Sc = 1f + 0.045f * Cp;
            float Sh = 1f + 0.015f * Cp * t;
            float Rt = -Mathf.Sin(2f * dRo * Mathf.Deg2Rad) * Rc;

            float sl = dLp / Sl;
            float sc = dCp / Sc;
            float sh = dHp / Sh;
            return Mathf.Sqrt(sl * sl + sc * sc + sh * sh + Rt * sc * sh);
        }
    }

    /// <summary>Convert bytes to luma. / 字节转亮度。</summary>
    [BurstCompile]
    public struct BytesToLumaJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Color32> src;
        [WriteOnly] public NativeArray<float> luma;

        public void Execute(int i)
        {
            var c = src[i];
            luma[i] = ATOColorMath.Luma709(c.r, c.g, c.b);
        }
    }

    /// <summary>Convert bytes to Lab (interleaved L,a,b). / 字节转 Lab（交错存储 L,a,b）。</summary>
    [BurstCompile]
    public struct BytesToLabJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Color32> src;
        [WriteOnly] public NativeArray<float> lab; // length = src*3

        public void Execute(int i)
        {
            var c = src[i];
            ATOColorMath.RgbToLab(c.r, c.g, c.b, out float L, out float A, out float B);
            lab[i * 3] = L;
            lab[i * 3 + 1] = A;
            lab[i * 3 + 2] = B;
        }
    }

    /// <summary>SSIM statistics maps (x, x*x, y, y*y, x*y) from two luma images. / 由两张亮度图生成 SSIM 统计图。</summary>
    [BurstCompile]
    public struct SsimStatMapsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> x;
        [ReadOnly] public NativeArray<float> y;
        [WriteOnly] public NativeArray<float> stats; // length = x*5

        public void Execute(int i)
        {
            float xv = x[i], yv = y[i];
            stats[i * 5] = xv;
            stats[i * 5 + 1] = xv * xv;
            stats[i * 5 + 2] = yv;
            stats[i * 5 + 3] = yv * yv;
            stats[i * 5 + 4] = xv * yv;
        }
    }

    /// <summary>Horizontal 11-tap Gaussian blur over 5-channel stat maps. / 5 通道统计图的水平 11 抽头高斯模糊。</summary>
    [BurstCompile]
    public struct GaussBlurHJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> src; // n*channels
        [WriteOnly] public NativeArray<float> dst;
        public int width, height, channels;
        [ReadOnly] public NativeArray<float> kernel; // 11 taps / 11 抽头

        public void Execute(int row)
        {
            int half = kernel.Length / 2;
            for (int i = 0; i < width; i++)
            {
                for (int cIdx = 0; cIdx < channels; cIdx++)
                {
                    float acc = 0f;
                    for (int k = -half; k <= half; k++)
                    {
                        int x = Mathf.Clamp(i + k, 0, width - 1);
                        acc += src[(row * width + x) * channels + cIdx] * kernel[k + half];
                    }
                    dst[(row * width + i) * channels + cIdx] = acc;
                }
            }
        }
    }

    /// <summary>Vertical 11-tap Gaussian blur over 5-channel stat maps. / 5 通道统计图的垂直 11 抽头高斯模糊。</summary>
    [BurstCompile]
    public struct GaussBlurVJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> src;
        [WriteOnly] public NativeArray<float> dst;
        public int width, height, channels;
        [ReadOnly] public NativeArray<float> kernel;

        public void Execute(int col)
        {
            int half = kernel.Length / 2;
            for (int i = 0; i < height; i++)
            {
                for (int cIdx = 0; cIdx < channels; cIdx++)
                {
                    float acc = 0f;
                    for (int k = -half; k <= half; k++)
                    {
                        int y = Mathf.Clamp(i + k, 0, height - 1);
                        acc += src[(y * width + col) * channels + cIdx] * kernel[k + half];
                    }
                    dst[(i * width + col) * channels + cIdx] = acc;
                }
            }
        }
    }

    /// <summary>Combine blurred stat maps into SSIM or CS map. / 由模糊统计图合成 SSIM/CS 图。</summary>
    [BurstCompile]
    public struct SsimCombineJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> stats; // blurred n*5 / 模糊后的 n*5
        [WriteOnly] public NativeArray<float> map;
        public bool contrastStructureOnly;

        public void Execute(int i)
        {
            float muX = stats[i * 5];
            float muX2 = stats[i * 5 + 1];
            float muY = stats[i * 5 + 2];
            float muY2 = stats[i * 5 + 3];
            float muXY = stats[i * 5 + 4];
            float varX = Mathf.Max(0f, muX2 - muX * muX);
            float varY = Mathf.Max(0f, muY2 - muY * muY);
            float cov = muXY - muX * muY;
            const float c1 = 0.0001f, c2 = 0.0009f, c3 = 0.00045f;
            if (contrastStructureOnly)
            {
                map[i] = (2f * cov + c3) / (varX + varY + c3);
            }
            else
            {
                map[i] = ((2f * muX * muY + c1) * (2f * cov + c2)) /
                         ((muX * muX + muY * muY + c1) * (varX + varY + c2));
            }
        }
    }

    /// <summary>2x2 box downsample of a luma map. / 亮度图的 2x2 盒式下采样。</summary>
    [BurstCompile]
    public struct DownsampleHalfJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> src;
        [WriteOnly] public NativeArray<float> dst;
        public int srcW, srcH;

        public void Execute(int i)
        {
            int dstW = Mathf.Max(1, srcW / 2);
            int dx = i % dstW;
            int dy = i / dstW;
            int sx = dx * 2, sy = dy * 2;
            float a = src[Mathf.Min(sy, srcH - 1) * srcW + Mathf.Min(sx, srcW - 1)];
            float b = src[Mathf.Min(sy, srcH - 1) * srcW + Mathf.Min(sx + 1, srcW - 1)];
            float c = src[Mathf.Min(sy + 1, srcH - 1) * srcW + Mathf.Min(sx, srcW - 1)];
            float d = src[Mathf.Min(sy + 1, srcH - 1) * srcW + Mathf.Min(sx + 1, srcW - 1)];
            dst[i] = (a + b + c + d) * 0.25f;
        }
    }

    /// <summary>2x2 OR-downsample of a coverage mask (islands keep coverage at coarse levels). / 覆盖掩码的 2x2 或下采样（粗层保留岛覆盖）。</summary>
    [BurstCompile]
    public struct MaskDownsampleJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> src;
        [WriteOnly] public NativeArray<byte> dst;
        public int srcW, srcH;

        public void Execute(int i)
        {
            int dstW = Mathf.Max(1, srcW / 2);
            int dx = i % dstW;
            int dy = i / dstW;
            int sx = dx * 2, sy = dy * 2;
            byte m = 0;
            m |= src[Mathf.Min(sy, srcH - 1) * srcW + Mathf.Min(sx, srcW - 1)];
            m |= src[Mathf.Min(sy, srcH - 1) * srcW + Mathf.Min(sx + 1, srcW - 1)];
            m |= src[Mathf.Min(sy + 1, srcH - 1) * srcW + Mathf.Min(sx, srcW - 1)];
            m |= src[Mathf.Min(sy + 1, srcH - 1) * srcW + Mathf.Min(sx + 1, srcW - 1)];
            dst[i] = m;
        }
    }

    /// <summary>DeltaE2000 map from two interleaved Lab images. / 由两张 Lab 图计算 ΔE2000 图。</summary>
    [BurstCompile]
    public struct DeltaE2000Job : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> labA;
        [ReadOnly] public NativeArray<float> labB;
        [WriteOnly] public NativeArray<float> de;

        public void Execute(int i)
        {
            de[i] = ATOColorMath.DeltaE2000(
                labA[i * 3], labA[i * 3 + 1], labA[i * 3 + 2],
                labB[i * 3], labB[i * 3 + 1], labB[i * 3 + 2]);
        }
    }

    /// <summary>Squared alpha diff map (linear spaces). / alpha 平方差图。</summary>
    [BurstCompile]
    public struct AlphaDiffJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Color32> a;
        [ReadOnly] public NativeArray<Color32> b;
        [WriteOnly] public NativeArray<float> diff;

        public void Execute(int i)
        {
            float d = (a[i].a - b[i].a) / 255f;
            diff[i] = d * d;
        }
    }

    /// <summary>Squared per-channel diff map for grayscale (used channel on bytes of linear data). / 灰度通道平方差图（线性数据字节）。</summary>
    [BurstCompile]
    public struct GrayDiffJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Color32> a;
        [ReadOnly] public NativeArray<Color32> b;
        [WriteOnly] public NativeArray<float> diff;
        public int channel; // 0=R,1=G,2=B,3=A

        public void Execute(int i)
        {
            byte va = 0, vb = 0;
            var ca = a[i];
            var cb = b[i];
            switch (channel)
            {
                case 0: va = ca.r; vb = cb.r; break;
                case 1: va = ca.g; vb = cb.g; break;
                case 2: va = ca.b; vb = cb.b; break;
                default: va = ca.a; vb = cb.a; break;
            }
            float d = (va - vb) / 255f;
            diff[i] = d * d;
        }
    }

    /// <summary>Angular difference map between two float normal fields, in degrees. / 两组浮点法线的角度差图（度）。</summary>
    [BurstCompile]
    public struct NormalAngleJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Color> a;
        [ReadOnly] public NativeArray<Color> b;
        [WriteOnly] public NativeArray<float> angle;

        public void Execute(int i)
        {
            var va = a[i];
            var vb = b[i];
            float dot = va.r * vb.r + va.g * vb.g + va.b * vb.b;
            dot = Mathf.Clamp(dot, -1f, 1f);
            angle[i] = Mathf.Acos(dot) * Mathf.Rad2Deg;
        }
    }

    /// <summary>Cutout silhouette maps for IoU (threshold at cutoff). / Cutout 轮廓图（按 cutoff 阈值化）。</summary>
    [BurstCompile]
    public struct CutoutMaskJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Color32> src;
        [WriteOnly] public NativeArray<byte> mask;
        public float cutoff;

        public void Execute(int i)
        {
            mask[i] = src[i].a / 255f >= cutoff ? (byte)1 : (byte)0;
        }
    }
}
