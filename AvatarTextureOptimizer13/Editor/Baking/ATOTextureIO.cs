// ATO — Avatar Texture Optimizer
// Texture I/O: reading pixels (with GPU fallback for non-readable textures), color-space
// conversion (sRGB→linear) and premultiplied-alpha helpers. Used by dedup and quality
// evaluation. All reads are cached in the build context to avoid repeated decode.
// 贴图 I/O：读取像素（对不可读贴图走 GPU 回退）、色彩空间转换（sRGB→linear）与预乘 alpha
// 辅助。供去重与质量评估使用。所有读取在构建上下文中缓存，避免重复解码。

using System;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Texture readback and color helpers. 贴图读回与颜色辅助。
    /// </summary>
    public static class ATOTextureIO
    {
        /// <summary>Get the TextureImporter for a texture asset (null for generated textures). 获取贴图资源的 TextureImporter（生成贴图为 null）。</summary>
        public static TextureImporter GetImporter(Texture2D tex)
        {
            if (tex == null) return null;
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetImporter.GetAtPath(path) as TextureImporter;
        }

        /// <summary>True if the texture is stored as sRGB. 贴图是否以 sRGB 存储。</summary>
        public static bool IsSRGB(Texture2D tex)
        {
            var importer = GetImporter(tex);
            if (importer != null) return importer.sRGBTexture;
            // Generated textures: assume sRGB for color-ish, linear for normal-ish. 生成贴图：默认按 sRGB 处理。
            return true;
        }

        /// <summary>
        /// Read pixels as RGBA32. Falls back to a GPU readback when the texture is not readable.
        /// 以 RGBA32 读取像素；贴图不可读时回退到 GPU 读回。
        /// </summary>
        public static bool TryReadPixels(Texture2D tex, out Color32[] rgba)
        {
            rgba = null;
            if (tex == null) return false;
            try
            {
                if (tex.isReadable)
                {
                    rgba = tex.GetPixels32();
                    return true;
                }
            }
            catch (Exception e)
            {
                ATOLog.Verbose($"GetPixels32 failed for '{tex.name}': {e.Message}");
            }

            // GPU fallback. GPU 回退。
            try
            {
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, false);
                copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                copy.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                rgba = copy.GetPixels32();
                UnityEngine.Object.DestroyImmediate(copy);
                return true;
            }
            catch (Exception e)
            {
                ATOLog.Warn($"GPU readback failed for '{tex.name}': {e.Message}");
                return false;
            }
        }

        /// <summary>Linearize a single channel. 单通道线性化。</summary>
        public static float Linear(float v, bool srgb)
        {
            return srgb ? Mathf.GammaToLinearSpace(v) : v;
        }

        /// <summary>
        /// Convert RGBA32 to linear, premultiplied-alpha float color.
        /// 将 RGBA32 转换为线性、预乘 alpha 的浮点颜色。
        /// </summary>
        public static Color ToLinearPremultiplied(Color32 c, bool srgb)
        {
            float r = srgb ? Mathf.GammaToLinearSpace(c.r / 255f) : c.r / 255f;
            float g = srgb ? Mathf.GammaToLinearSpace(c.g / 255f) : c.g / 255f;
            float b = srgb ? Mathf.GammaToLinearSpace(c.b / 255f) : c.b / 255f;
            float a = c.a / 255f;
            return new Color(r * a, g * a, b * a, a);
        }

        /// <summary>
        /// Decode a normal map texel into a unit vector (DXT5nm-style: x=a, y=g, z=sqrt(1-x²-y²)).
        /// 将法线贴图像素解码为单位向量（DXT5nm 风格：x=a, y=g, z=sqrt(1-x²-y²)）。
        /// </summary>
        public static Vector3 DecodeNormal(Color32 c)
        {
            float x = c.a / 255f * 2f - 1f;
            float y = c.g / 255f * 2f - 1f;
            float z = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y));
            return new Vector3(x, y, z).normalized;
        }

        /// <summary>
        /// Encode a unit vector back into a normal map texel. 将单位向量重新编码为法线贴图像素。
        /// </summary>
        public static Color32 EncodeNormal(Vector3 n)
        {
            float x = Mathf.Clamp(n.x, -1f, 1f);
            float y = Mathf.Clamp(n.y, -1f, 1f);
            return new Color32(
                0,
                (byte)Mathf.RoundToInt((y * 0.5f + 0.5f) * 255f),
                0,
                (byte)Mathf.RoundToInt((x * 0.5f + 0.5f) * 255f));
        }

        /// <summary>
        /// True when the two materials have identical tiling/offset for a texture property.
        /// 两个材质对某贴图属性的平铺/偏移是否一致。
        /// </summary>
        public static bool HasNonIdentitySTSafe(Material a, string prop, Material b)
        {
            try
            {
                return a.GetTextureScale(prop) == b.GetTextureScale(prop) &&
                       a.GetTextureOffset(prop) == b.GetTextureOffset(prop);
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>Estimate the texture's memory footprint in bytes (uncompressed). 估算贴图内存（未压缩）。</summary>
        public static long EstimateBytes(Texture2D tex)
        {
            if (tex == null) return 0;
            return (long)tex.width * tex.height * 4;
        }
    }
}
