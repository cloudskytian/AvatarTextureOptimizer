using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// CPU resampler for normal maps: decode (Unity RGorAG) → bilinear filter in unit-vector space
    /// → renormalize → re-encode. Tangents are never touched anywhere in ATO.
    /// / 法线贴图CPU重采样：解码→单位向量空间双线性→重归一化→重编码。ATO 全程绝不重算切线。
    /// </summary>
    internal static class NormalResampler
    {
        internal static Color32[] Downsample(Color32[] src, int w, int h, int dw, int dh)
        {
            var vectors = Decode(src);
            var filtered = SampleBilinear(vectors, w, h, dw, dh);
            return Encode(filtered, dw * dh);
        }

        internal static Color32[] Upsample(Color32[] src, int w, int h, int dw, int dh)
        {
            var vectors = Decode(src);
            var filtered = SampleBilinear(vectors, w, h, dw, dh);
            // Upscale keeps normalization to mimic sampler behavior / 上采样同样归一化以贴近采样行为
            return Encode(filtered, dw * dh);
        }

        private static Vector3[] Decode(Color32[] src)
        {
            var v = new Vector3[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var c = src[i];
                // Unity UnpackNormalmapRGorAG / 与Unity解包一致
                float x = (c.r / 255f) * (c.a / 255f) * 2f - 1f;
                float y = (c.g / 255f) * 2f - 1f;
                float z = Mathf.Sqrt(Mathf.Clamp01(1f - x * x - y * y));
                v[i] = new Vector3(x, y, z);
            }
            return v;
        }

        private static Vector3[] SampleBilinear(Vector3[] src, int w, int h, int dw, int dh)
        {
            var dst = new Vector3[dw * dh];
            float sx = w / (float)dw, sy = h / (float)dh;
            for (int y = 0; y < dh; y++)
            {
                // pixel centers / 像素中心
                float fy = (y + 0.5f) * sy - 0.5f;
                int y0 = Mathf.Clamp((int)Mathf.Floor(fy), 0, h - 1);
                int y1 = Mathf.Min(y0 + 1, h - 1);
                float ty = Mathf.Clamp01(fy - y0);
                for (int x = 0; x < dw; x++)
                {
                    float fx = (x + 0.5f) * sx - 0.5f;
                    int x0 = Mathf.Clamp((int)Mathf.Floor(fx), 0, w - 1);
                    int x1 = Mathf.Min(x0 + 1, w - 1);
                    float tx = Mathf.Clamp01(fx - x0);

                    var a = src[y0 * w + x0];
                    var b = src[y0 * w + x1];
                    var c = src[y1 * w + x0];
                    var d = src[y1 * w + x1];
                    dst[y * dw + x] = Vector3.Lerp(Vector3.Lerp(a, b, tx), Vector3.Lerp(c, d, tx), ty);
                }
            }
            return dst;
        }

        private static Color32[] Encode(Vector3[] v, int count)
        {
            var bytes = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                var n = v[i];
                // renormalize / 重归一化
                float len = n.magnitude;
                if (len > 1e-5f) n /= len;
                else n = new Vector3(0f, 0f, 1f); // flat normal fallback / 平坦法线兜底

                // Canonical layout r=x, g=y, b=z, a=1: BC5 keeps rg (UnpackNormalmapRGorAG
                // resolves x via r*a), mobile ASTC/unpacked paths read rgb directly.
                // / 规范布局 r=x,g=y,b=z,a=1：BC5 取 rg（RGorAG 解包兼容），移动端直接读 rgb。
                bytes[i] = new Color32(
                    (byte)Mathf.Round(Mathf.Clamp01(n.x * 0.5f + 0.5f) * 255f),
                    (byte)Mathf.Round(Mathf.Clamp01(n.y * 0.5f + 0.5f) * 255f),
                    (byte)Mathf.Round(Mathf.Clamp01(n.z * 0.5f + 0.5f) * 255f),
                    255);
            }
            return bytes;
        }
    }
}
