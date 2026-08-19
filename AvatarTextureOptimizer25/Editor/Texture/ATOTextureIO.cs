// Avatar Texture Optimizer / 头像贴图优化器
// Texture import-metadata extraction, GPU readback and content hashing.
// 贴图导入元数据提取、GPU 回读与内容哈希。
//
// Content hashing decodes the texture through the GPU to RGBA32 rows and
// incrementally hashes with SHA-256. Done sequentially with one reusable
// buffer to keep memory in check on low-end machines.
// 内容哈希经 GPU 解码为 RGBA32 后用 SHA-256 增量计算；全程顺序执行并复用
// 单个缓冲区，控制低配机器上的内存占用。

using System;
using System.Security.Cryptography;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Texture import metadata + readback helpers. / 贴图导入元数据与回读工具。</summary>
    public static class ATOTextureIO
    {
        /// <summary>
        /// Fill a texture entry's import metadata from the TextureImporter.
        /// 从 TextureImporter 填充贴图表项的导入元数据。
        /// </summary>
        public static void FillImportMetadata(ATOTextureEntry entry)
        {
            var tex = entry.texture;
            if (tex == null) return;
            entry.width = tex.width;
            entry.height = tex.height;
            entry.format = tex.format;
            entry.filterMode = tex.filterMode;
            entry.assetPath = AssetDatabase.GetAssetPath(tex);
            entry.hasRealAlpha = TextureFormatHasAlpha(tex.format);

            var path = entry.assetPath;
            var importer = !string.IsNullOrEmpty(path) ? AssetImporter.GetAtPath(path) as TextureImporter : null;
            if (importer != null)
            {
                entry.sRGB = importer.sRGBTexture;
                entry.isNormalMap = importer.textureType == TextureImporterType.NormalMap;
                entry.alphaIsTransparency = importer.alphaIsTransparency;
                entry.mipmapsEnabled = importer.mipmapEnabled;
                entry.streamingMipmaps = importer.streamingMipmaps;
                entry.filterMode = importer.filterMode != FilterMode.Bilinear && importer.filterMode != FilterMode.Point
                    ? importer.filterMode
                    : importer.filterMode;
                entry.wrapModeU = importer.wrapU;
                entry.wrapModeV = importer.wrapV;
                entry.importSignature = BuildImportSignature(importer, tex);
                entry.sourceBytes = EstimateSourceBytes(tex, path);
            }
            else
            {
                // In-memory / generated texture: use runtime state instead. / 内存/生成贴图：用运行时状态。
                entry.sRGB = !UnityEngine.PlayerSettings.colorSpace.Equals(ColorSpace.Gamma) || TextureFormatLooksSrgb(tex.format);
                entry.isNormalMap = TextureFormatIsNormalMap(tex.format);
                entry.alphaIsTransparency = entry.hasRealAlpha;
                entry.mipmapsEnabled = tex.mipmapCount > 1;
                entry.streamingMipmaps = false;
                entry.wrapModeU = tex.wrapU;
                entry.wrapModeV = tex.wrapV;
                entry.importSignature = $"mem|{tex.width}x{tex.height}|{tex.format}|{tex.filterMode}|{tex.wrapU}|{tex.wrapV}";
                entry.sourceBytes = EstimateSourceBytes(tex, null);
            }
        }

        private static long EstimateSourceBytes(Texture2D tex, string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    return new System.IO.FileInfo(path).Length;
            }
            catch { /* best effort */ }
            return (long)tex.width * tex.height * 4;
        }

        /// <summary>Signature of import settings (dedup gate: any difference = different texture). / 导入设置签名（去重门槛：任何差异=不同贴图）。</summary>
        public static string BuildImportSignature(TextureImporter imp, Texture2D tex)
        {
            var sb = new StringBuilder(128);
            sb.Append(tex.width).Append('x').Append(tex.height);
            sb.Append("|t").Append((int)imp.textureType);
            sb.Append("|s").Append(imp.sRGBTexture ? 1 : 0);
            sb.Append("|a").Append(imp.alphaIsTransparency ? 1 : 0);
            sb.Append("|f").Append((int)imp.filterMode);
            sb.Append("|wu").Append((int)imp.wrapU).Append("|wv").Append((int)imp.wrapV);
            sb.Append("|m").Append(imp.mipmapEnabled ? 1 : 0);
            sb.Append("|ms").Append(imp.streamingMipmaps ? 1 : 0);
            sb.Append("|n").Append((int)imp.npotScale);
            sb.Append("|tc").Append((int)imp.textureCompression);
            return sb.ToString();
        }

        public static bool TextureFormatHasAlpha(TextureFormat f)
        {
            switch (f)
            {
                case TextureFormat.Alpha8:
                case TextureFormat.ARGB32:
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB4444:
                case TextureFormat.RGBA4444:
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.BC4:
                case TextureFormat.BC5:
                case TextureFormat.BC6H:
                case TextureFormat.BC7:
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.ETC2_RGBA8Crunched:
                case TextureFormat.ETC2_RGBA1:
                case TextureFormat.PVRTC_RGBA2:
                case TextureFormat.PVRTC_RGBA4:
                case TextureFormat.ASTC_4x4:
                case TextureFormat.ASTC_5x5:
                case TextureFormat.ASTC_6x6:
                case TextureFormat.ASTC_8x8:
                case TextureFormat.ASTC_10x10:
                case TextureFormat.ASTC_12x12:
                case TextureFormat.ASTC_RGBA_4x4:
                case TextureFormat.ASTC_RGBA_5x5:
                case TextureFormat.ASTC_RGBA_6x6:
                case TextureFormat.ASTC_RGBA_8x8:
                case TextureFormat.ASTC_RGBA_10x10:
                case TextureFormat.ASTC_RGBA_12x12:
                case TextureFormat.RGBAFloat:
                case TextureFormat.RGBAHalf:
                case TextureFormat.RGFloat:
                case TextureFormat.RGHalf:
                case TextureFormat.R8:
                case TextureFormat.R16:
                case TextureFormat.RFloat:
                case TextureFormat.RHalf:
                    return true;
                default:
                    return false;
            }
        }

        private static bool TextureFormatIsNormalMap(TextureFormat f) =>
            f == TextureFormat.DXT5nm || f == TextureFormat.BC5;

        private static bool TextureFormatLooksSrgb(TextureFormat f)
        {
            // Compressed color formats are treated as sRGB. / 压缩色彩格式视为 sRGB。
            switch (f)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT5:
                case TextureFormat.BC7:
                case TextureFormat.ETC_RGB4:
                case TextureFormat.ETC2_RGB4:
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.PVRTC_RGB2:
                case TextureFormat.PVRTC_RGB4:
                case TextureFormat.PVRTC_RGBA2:
                case TextureFormat.PVRTC_RGBA4:
                case TextureFormat.ASTC_4x4:
                case TextureFormat.ASTC_6x6:
                case TextureFormat.ASTC_8x8:
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------
        // GPU readback / GPU 回读
        // ------------------------------------------------------------------

        /// <summary>
        /// Decode to a readable RGBA32 copy (respecting sRGB decode as Unity does).
        /// 解码为可读的 RGBA32 副本（遵循 Unity 的 sRGB 解码）。
        /// Caller owns the returned Texture2D. / 返回的 Texture2D 由调用方负责销毁。
        /// </summary>
        public static Texture2D Readback(Texture2D tex, ATORtPool pool = null)
        {
            if (tex == null) return null;
            // The RT read/write space must MATCH the texture's sRGB-ness or the
            // round trip is not identity in linear-color-space projects:
            //   sRGB tex -> sRGB RT: sampler decodes, writer encodes  (identity)
            //   linear tex -> linear RT: no conversion anywhere        (identity)
            // A linear texture blitted into an sRGB RT gets sRGB-ENCODED bytes
            // (QA-1 finding: broke mask/gray metric comparisons).
            // RT 读写空间必须与贴图的 sRGB 属性一致，否则在线性彩空间工程里往返
            // 不再是恒等映射：线性贴图 blit 进 sRGB RT 会被 sRGB 编码
            // （QA-1 发现：破坏蒙版/灰度指标对比）。
            bool srgbTex = UnityEngine.Experimental.Rendering.GraphicsFormatUtility
                .IsSRGBFormat(tex.graphicsFormat);
            var rw = srgbTex ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
            RenderTexture rt = null;
            bool pooled = false;
            try
            {
                if (pool != null) { rt = pool.Rent(tex.width, tex.height, RenderTextureFormat.ARGB32, rw); pooled = true; }
                else
                {
                    rt = new RenderTexture(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, rw);
                    rt.Create();
                }
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, false);
                copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0, false);
                copy.Apply(false, false);
                RenderTexture.active = prev;
                return copy;
            }
            finally
            {
                if (rt != null)
                {
                    if (pooled && pool != null) pool.Return(rt);
                    else { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                }
            }
        }

        /// <summary>
        /// SHA-256 over raw RGBA32 bytes (chunked, allocation-light).
        /// 对 RGBA32 原始字节做 SHA-256（分块、低分配）。
        /// </summary>
        public static string ContentHash(Texture2D decoded)
        {
            if (decoded == null) return null;
            var raw = decoded.GetRawTextureData<byte>();
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                unsafe
                {
                    var ptr = (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(raw);
                    const int chunk = 1 << 20;
                    int left = raw.Length;
                    int offset = 0;
                    var tmp = new byte[chunk];
                    while (left > 0)
                    {
                        int n = Math.Min(chunk, left);
                        System.Runtime.InteropServices.Marshal.Copy((IntPtr)(ptr + offset), tmp, 0, n);
                        sha.AppendData(tmp, 0, n);
                        left -= n;
                        offset += n;
                    }
                }
                var hash = sha.GetHashAndReset();
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return $"{decoded.width}x{decoded.height}:{sb}";
            }
        }
    }
}
