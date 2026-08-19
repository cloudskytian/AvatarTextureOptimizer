using System;
using UnityEditor;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    internal static class ATOTextureUtil
    {
        /// <summary>
        /// Decode via Blit so the source does not need Read/Write.
        /// 用 Blit 解码，源贴图不必开 Read/Write。
        /// </summary>
        public static ATODecodedTexture Decode(ATOContext ctx, Texture2D src)
        {
            if (src == null) return null;
            if (ctx.DecodeCache.TryGetValue(src, out var cached) && cached != null && !cached.Disposed)
                return cached;

            var linear = GuessLinear(src);
            var rt = ATOGpuUtil.GetRT(src.width, src.height, RenderTextureFormat.ARGBFloat, linear);
            var mat = ATOGpuUtil.GetMaterial("Hidden/ATO/Copy");
            if (mat != null) ATOGpuUtil.Blit(src, rt, mat);
            else Graphics.Blit(src, rt);

            var tmp = ATOGpuUtil.ReadRT(rt, linear);
            var pixels = tmp.GetPixels();
            UnityEngine.Object.DestroyImmediate(tmp);

            var dec = new ATODecodedTexture
            {
                Source = src,
                Width = src.width,
                Height = src.height,
                Pixels = pixels,
                Linear = linear,
                HasAlpha = HasMeaningfulAlpha(pixels),
                IsNormal = IsNormalImporter(src)
            };
            ctx.DecodeCache[src] = dec;
            ctx.Log.Detail($"Decode '{src.name}' {src.width}x{src.height} linear={linear} alpha={dec.HasAlpha} normal={dec.IsNormal}");
            return dec;
        }

        public static bool GuessLinear(Texture2D tex)
        {
            if (IsNormalImporter(tex)) return true;
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return false;
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return false;
            return imp.sRGBTexture == false;
        }

        public static bool IsNormalImporter(Texture2D tex)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return false;
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            return imp != null && imp.textureType == TextureImporterType.NormalMap;
        }

        public static TextureImporter GetImporter(Texture tex)
        {
            if (tex == null) return null;
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetImporter.GetAtPath(path) as TextureImporter;
        }

        public static string ImportFingerprint(Texture2D tex)
        {
            var imp = GetImporter(tex);
            if (imp == null) return "noimporter";
            return string.Join("|",
                imp.textureType, imp.sRGBTexture, imp.mipmapEnabled, imp.streamingMipmaps,
                imp.filterMode, imp.wrapMode, imp.anisoLevel, imp.npotScale,
                imp.textureCompression, imp.crunchedCompression, imp.compressionQuality,
                imp.maxTextureSize, imp.alphaSource, imp.alphaIsTransparency);
        }

        public static bool HasMeaningfulAlpha(Color[] px)
        {
            if (px == null || px.Length == 0) return false;
            // Sample a stride to stay cheap. / 抽样，控制成本。
            var step = Math.Max(1, px.Length / 4096);
            var min = 1f;
            var max = 0f;
            for (int i = 0; i < px.Length; i += step)
            {
                var a = px[i].a;
                if (a < min) min = a;
                if (a > max) max = a;
                if (max - min > 0.02f) return true;
            }
            return max - min > 0.02f;
        }

        public static bool IsSolidColor(Color[] px, out Color solid)
        {
            solid = default;
            if (px == null || px.Length == 0) return false;
            var c0 = px[0];
            const float eps = 1e-3f;
            for (int i = 1; i < px.Length; i++)
            {
                var d = px[i] - c0;
                if (Mathf.Abs(d.r) > eps || Mathf.Abs(d.g) > eps || Mathf.Abs(d.b) > eps || Mathf.Abs(d.a) > eps)
                    return false;
            }
            solid = c0;
            return true;
        }

        public static Color Bilinear(Color[] px, int w, int h, float u, float v)
        {
            u = Mathf.Clamp(u, 0f, w - 1.0001f);
            v = Mathf.Clamp(v, 0f, h - 1.0001f);
            var x0 = (int)u;
            var y0 = (int)v;
            var x1 = Math.Min(x0 + 1, w - 1);
            var y1 = Math.Min(y0 + 1, h - 1);
            var fx = u - x0;
            var fy = v - y0;
            var c00 = px[y0 * w + x0];
            var c10 = px[y0 * w + x1];
            var c01 = px[y1 * w + x0];
            var c11 = px[y1 * w + x1];
            return Color.Lerp(Color.Lerp(c00, c10, fx), Color.Lerp(c01, c11, fx), fy);
        }

        public static ulong PixelHash(Color[] px)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                if (px == null) return h;
                var step = Math.Max(1, px.Length / 65536);
                for (int i = 0; i < px.Length; i += step)
                {
                    var c = px[i];
                    h ^= (ulong)(c.r * 16777216f);
                    h *= 1099511628211UL;
                    h ^= (ulong)(c.g * 16777216f);
                    h *= 1099511628211UL;
                    h ^= (ulong)(c.b * 16777216f);
                    h *= 1099511628211UL;
                    h ^= (ulong)(c.a * 16777216f);
                    h *= 1099511628211UL;
                }
                h ^= (ulong)px.Length;
                return h;
            }
        }

        /// <summary>
        /// Swizzle tangent-space RG after a 90° CW rotation of the island image.
        /// 岛图像顺时针 90° 后，对切线空间 RG 做 swizzle。网格切线绝不重算。
        /// n' = (-n.y, n.x) for 90° CW in Unity texture space (V up).
        /// </summary>
        public static Color SwizzleNormal90Cw(Color n)
        {
            var x = n.r * 2f - 1f;
            var y = n.g * 2f - 1f;
            var nx = -y;
            var ny = x;
            return new Color(nx * 0.5f + 0.5f, ny * 0.5f + 0.5f, n.b, n.a);
        }

        public static Color SwizzleNormal90Ccw(Color n)
        {
            var x = n.r * 2f - 1f;
            var y = n.g * 2f - 1f;
            var nx = y;
            var ny = -x;
            return new Color(nx * 0.5f + 0.5f, ny * 0.5f + 0.5f, n.b, n.a);
        }
    }
}
