// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using UnityEditor;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Texture
{
    /// <summary>
    /// Reads a texture's pixels and import settings, producing an enriched
    /// <see cref="ATOTextureRecord"/> used for dedup, category grouping and optimization.
    /// Pixel data is decoded into linear space (premultiplied where relevant) once and cached.
    ///
    /// 读取贴图像素与导入设置，生成用于去重、类别分组与优化的 ATOTextureRecord。
    /// 像素一次性解码为线性空间（必要时预乘）并缓存。
    /// </summary>
    public static class ATOTextureReader
    {
        /// <summary>
        /// Build a record for a texture. Reads pixels (cached), import settings, and computes
        /// a content hash. Returns null if the texture is not readable (→ whitelist).
        ///
        /// 为贴图构建记录。读取像素（缓存）、导入设置并计算内容哈希。
        /// 若贴图不可读则返回 null（→ 白名单）。
        /// </summary>
        public static ATOTextureRecord Read(Texture2D tex)
        {
            if (tex == null) return null;

            var rec = new ATOTextureRecord { Texture = tex };

            rec.AssetPath = AssetDatabase.GetAssetPath(tex);
            rec.Width = tex.width;
            rec.Height = tex.height;

            // Import settings (for asset textures). 导入设置（针对资产贴图）。
            var importer = string.IsNullOrEmpty(rec.AssetPath)
                ? null
                : AssetImporter.GetAtPath(rec.AssetPath) as TextureImporter;

            if (importer != null)
            {
                rec.IsSrgb = importer.sRGBTexture;
                rec.FilterMode = importer.filterMode;
                rec.WrapMode = importer.wrapMode;
                rec.HasMipmaps = importer.mipmapEnabled;
            }
            else
            {
                // Runtime / non-asset texture: fall back to its current state.
                // 运行时/非资产贴图：回退到当前状态。
                rec.IsSrgb = true;
                rec.FilterMode = tex.filterMode;
                rec.WrapMode = tex.wrapMode;
                rec.HasMipmaps = false;
            }

            // Read pixels. 读取像素。
            try
            {
                rec.Pixels32 = tex.GetPixels32();
            }
            catch (UnityException)
            {
                ATOLog.Warning($"Texture {tex.name} is not readable; treating as whitelist. / " +
                               $"贴图 {tex.name} 不可读，按白名单处理。");
                return null;
            }

            // Content hash (FNV-1a over raw bytes). 内容哈希（FNV-1a）。
            rec.ContentHash = Fnv1a(rec.Pixels32);

            // Alpha detection. alpha 检测。
            rec.HasAlpha = false;
            foreach (var c in rec.Pixels32)
            {
                if (c.a != 255) { rec.HasAlpha = true; break; }
            }

            // Decode to linear space. 解码为线性空间。
            rec.Pixels = new Color[rec.Pixels32.Length];
            for (int i = 0; i < rec.Pixels32.Length; i++)
            {
                var c = rec.Pixels32[i];
                float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f, a = c.a / 255f;
                if (rec.IsSrgb)
                {
                    r = SrgbToLinear(r);
                    g = SrgbToLinear(g);
                    b = SrgbToLinear(b);
                }
                rec.Pixels[i] = new Color(r, g, b, a);
            }

            // Import signature = settings + content hash. 导入签名 = 设置 + 内容哈希。
            rec.ImportSignature =
                $"{rec.IsSrgb}|{(int)rec.FilterMode}|{(int)rec.WrapMode}|{rec.HasMipmaps}|" +
                $"{System.BitConverter.ToString(rec.ContentHash).Replace("-", "")}";

            return rec;
        }

        /// <summary>sRGB → linear. sRGB 转线性。</summary>
        public static float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        /// <summary>Linear → sRGB. 线性转 sRGB。</summary>
        public static float LinearToSrgb(float c)
        {
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        private static byte[] Fnv1a(Color32[] pixels)
        {
            const uint prime = 16777619;
            uint hash = 2166136261;
            foreach (var c in pixels)
            {
                hash ^= c.r; hash *= prime;
                hash ^= c.g; hash *= prime;
                hash ^= c.b; hash *= prime;
                hash ^= c.a; hash *= prime;
            }

            var bytes = new byte[4];
            bytes[0] = (byte)(hash & 0xFF);
            bytes[1] = (byte)((hash >> 8) & 0xFF);
            bytes[2] = (byte)((hash >> 16) & 0xFF);
            bytes[3] = (byte)((hash >> 24) & 0xFF);
            return bytes;
        }
    }
}
