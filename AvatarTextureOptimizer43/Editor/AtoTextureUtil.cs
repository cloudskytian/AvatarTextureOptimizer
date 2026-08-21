using System;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// GPU-friendly texture IO. Never assumes isReadable.
    /// 贴图读写。不依赖 isReadable。
    /// </summary>
    public static class AtoTextureUtil
    {
        static Material _blitMat;

        public static Color[] ReadPixels(Texture tex, bool linear)
        {
            if (tex == null) return Array.Empty<Color>();
            int w = tex.width, h = tex.height;
            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGBFloat, 0)
            {
                sRGB = !linear,
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            var rt = RenderTexture.GetTemporary(desc);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var tmp = new Texture2D(w, h, TextureFormat.RGBAFloat, false, linear);
                tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                tmp.Apply(false, false);
                var pixels = tmp.GetPixels();
                UnityEngine.Object.DestroyImmediate(tmp);
                return pixels;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        public static Color[] ReadPixels(Texture2D tex)
        {
            bool linear = !GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat);
            return ReadPixels(tex, linear);
        }

        /// <summary>
        /// Content + importer-settings identity. Different importer settings => different textures.
        /// 像素内容 + 导入设置。导入设置不同即视为不同贴图。
        /// </summary>
        public static string ContentHash(Texture2D tex)
        {
            if (tex == null) return "null";
            var pixels = ReadPixels(tex);
            using (var md5 = MD5.Create())
            {
                var bytes = new byte[pixels.Length * 4];
                // Hash a downsampled fingerprint plus size to keep memory/CPU bounded.
                // 用降采样指纹 + 尺寸，避免超大贴图爆内存。
                int step = Math.Max(1, pixels.Length / 65536);
                var buf = new byte[(pixels.Length / step + 8) * 4];
                int o = 0;
                WriteInt(buf, ref o, tex.width);
                WriteInt(buf, ref o, tex.height);
                for (int i = 0; i < pixels.Length; i += step)
                {
                    var c = pixels[i];
                    buf[o++] = (byte)Mathf.Clamp(c.r * 255f, 0, 255);
                    buf[o++] = (byte)Mathf.Clamp(c.g * 255f, 0, 255);
                    buf[o++] = (byte)Mathf.Clamp(c.b * 255f, 0, 255);
                    buf[o++] = (byte)Mathf.Clamp(c.a * 255f, 0, 255);
                }
                var hash = md5.ComputeHash(buf, 0, o);
                var importer = ImporterFingerprint(tex);
                return BitConverter.ToString(hash).Replace("-", "") + ":" + importer;
            }
        }

        public static string ImporterFingerprint(Texture2D tex)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            var ti = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti != null)
            {
                return string.Join("|",
                    ti.sRGBTexture, ti.textureType, ti.filterMode, ti.wrapMode, ti.wrapModeU, ti.wrapModeV,
                    ti.anisoLevel, ti.mipmapEnabled, ti.streamingMipmaps, ti.maxTextureSize,
                    ti.textureCompression, ti.crunchedCompression, ti.compressionQuality,
                    ti.npotScale, ti.alphaSource, ti.alphaIsTransparency, ti.fadeout, ti.mipMapBias,
                    ti.textureShape, ti.nPOTScale());
            }
            return string.Join("|",
                tex.graphicsFormat, tex.filterMode, tex.wrapMode, tex.wrapModeU, tex.wrapModeV,
                tex.anisoLevel, tex.mipmapCount, tex.format, tex.width, tex.height);
        }

        static int nPOTScale(this TextureImporter ti)
        {
            return (int)ti.npotScale;
        }

        static void WriteInt(byte[] b, ref int o, int v)
        {
            b[o++] = (byte)(v & 0xff);
            b[o++] = (byte)((v >> 8) & 0xff);
            b[o++] = (byte)((v >> 16) & 0xff);
            b[o++] = (byte)((v >> 24) & 0xff);
        }

        public static bool IsSolidColor(Color[] px, float eps = 1e-3f)
        {
            if (px == null || px.Length == 0) return true;
            var c0 = px[0];
            for (int i = 1; i < px.Length; i++)
            {
                var d = px[i] - c0;
                if (Mathf.Abs(d.r) > eps || Mathf.Abs(d.g) > eps || Mathf.Abs(d.b) > eps || Mathf.Abs(d.a) > eps)
                    return false;
            }
            return true;
        }

        public static bool HasAlpha(Color[] px, float eps = 1f / 255f)
        {
            if (px == null) return false;
            for (int i = 0; i < px.Length; i++)
                if (px[i].a < 1f - eps) return true;
            return false;
        }

        /// <summary>
        /// Bilinear resample. Premultiply alpha when `premul` (required for transparent downsample).
        /// 双线性重采样。透明贴图下采样时预乘 alpha。
        /// </summary>
        public static Color[] Resample(Color[] src, int sw, int sh, int dw, int dh, bool premul, bool linearizeSrgb)
        {
            var dst = new Color[dw * dh];
            if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) return dst;
            for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                float u = (x + 0.5f) * sw / dw - 0.5f;
                float v = (y + 0.5f) * sh / dh - 0.5f;
                dst[y * dw + x] = SampleBilinear(src, sw, sh, u, v, premul, linearizeSrgb);
            }
            return dst;
        }

        public static Color SampleBilinear(Color[] src, int w, int h, float u, float v, bool premul, bool linearize)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(u), 0, w - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(v), 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1);
            int y1 = Mathf.Min(y0 + 1, h - 1);
            float tx = Mathf.Clamp01(u - x0);
            float ty = Mathf.Clamp01(v - y0);
            var c00 = Prep(src[y0 * w + x0], premul, linearize);
            var c10 = Prep(src[y0 * w + x1], premul, linearize);
            var c01 = Prep(src[y1 * w + x0], premul, linearize);
            var c11 = Prep(src[y1 * w + x1], premul, linearize);
            var c0 = Color.Lerp(c00, c10, tx);
            var c1 = Color.Lerp(c01, c11, tx);
            var c = Color.Lerp(c0, c1, ty);
            if (premul && c.a > 1e-6f)
            {
                c.r /= c.a; c.g /= c.a; c.b /= c.a;
            }
            if (linearize)
            {
                c.r = Mathf.LinearToGammaSpace(c.r);
                c.g = Mathf.LinearToGammaSpace(c.g);
                c.b = Mathf.LinearToGammaSpace(c.b);
            }
            return c;
        }

        static Color Prep(Color c, bool premul, bool linearize)
        {
            if (linearize)
            {
                c.r = Mathf.GammaToLinearSpace(c.r);
                c.g = Mathf.GammaToLinearSpace(c.g);
                c.b = Mathf.GammaToLinearSpace(c.b);
            }
            if (premul)
            {
                c.r *= c.a; c.g *= c.a; c.b *= c.a;
            }
            return c;
        }

        public static Texture2D Create(string name, int w, int h, Color[] px, bool linear, bool mips)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mips, linear)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 1
            };
            tex.SetPixels(px);
            tex.Apply(mips, false);
            return tex;
        }

        /// <summary>
        /// Decode tangent-space normals, bilinear resample, renormalize, encode back to RGB.
        /// 法线解码 → 重采样 → 重归一化 → 再编码。切线数据本身绝不重算。
        /// </summary>
        public static Color[] ResampleNormal(Color[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color[dw * dh];
            if (sw < 1 || sh < 1 || dw < 1 || dh < 1) return dst;
            for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                float u = (x + 0.5f) * sw / dw - 0.5f;
                float v = (y + 0.5f) * sh / dh - 0.5f;
                var n = SampleNormal(src, sw, sh, u, v);
                n.Normalize();
                dst[y * dw + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
            }
            return dst;
        }

        static Vector3 SampleNormal(Color[] src, int w, int h, float u, float v)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(u), 0, w - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(v), 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1);
            int y1 = Mathf.Min(y0 + 1, h - 1);
            float tx = Mathf.Clamp01(u - x0);
            float ty = Mathf.Clamp01(v - y0);
            var n00 = DecN(src[y0 * w + x0]);
            var n10 = DecN(src[y0 * w + x1]);
            var n01 = DecN(src[y1 * w + x0]);
            var n11 = DecN(src[y1 * w + x1]);
            return Vector3.Lerp(Vector3.Lerp(n00, n10, tx), Vector3.Lerp(n01, n11, tx), ty);
        }

        static Vector3 DecN(Color c)
        {
            float x = c.r * 2f - 1f, y = c.g * 2f - 1f, z = c.b * 2f - 1f;
            if (c.b < 0.01f && c.a > 0.01f)
            {
                x = c.a * 2f - 1f; y = c.g * 2f - 1f;
                z = Mathf.Sqrt(Mathf.Max(0, 1 - x * x - y * y));
            }
            var n = new Vector3(x, y, z);
            return n.sqrMagnitude < 1e-8f ? Vector3.forward : n;
        }

        public static long UncompressedBytes(Texture t)
        {
            if (t == null) return 0;
            // RGBA32 estimate with mips (~1.33x). RGBA32 估算含 mip。
            return (long)t.width * t.height * 4 * 4 / 3;
        }
    }
}
