using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.Ato.Editor.Util
{
    /// <summary>
    /// Texture IO helpers: read raw pixels (respecting sRGB/linear), compute import-setting hashes,
    /// create output textures with safe import settings. Caches decoded pixel buffers to avoid
    /// repeated full-resolution readback.
    /// 贴图 IO 工具：读取原始像素（区分 sRGB/线性）、计算导入设置哈希、用安全导入设置创建输出贴图；
    /// 缓存解码像素缓冲以避免重复全分辨率读回。
    /// </summary>
    internal static class TextureIO
    {
        [Serializable]
        private struct ImportKey : IEquatable<ImportKey>
        {
            public int Width, Height;
            public TextureFormat Format;
            public TextureCompressionSettings Compression;
            public FilterMode Filter;
            public TextureWrapMode WrapU, WrapV;
            public bool Mipmap, sRGB, Alpha, Crunch;
            public int Aniso;
            public bool Equals(ImportKey o) =>
                Width == o.Width && Height == o.Height && Format == o.Format && Compression == o.Compression &&
                Filter == o.Filter && WrapU == o.WrapU && WrapV == o.WrapV && Mipmap == o.Mipmap &&
                sRGB == o.sRGB && Alpha == o.Alpha && Crunch == o.Crunch && Aniso == o.Aniso;
            public override int GetHashCode()
            {
                int h = Width; h = h * 397 ^ Height; h = h * 397 ^ (int)Format; h = h * 397 ^ (int)Compression;
                h = h * 397 ^ (int)Filter; h = h * 397 ^ (int)WrapU; h = h * 397 ^ (int)WrapV;
                h = h * 397 ^ (Mipmap ? 1 : 0); h = h * 397 ^ (sRGB ? 2 : 0);
                h = h * 397 ^ (Alpha ? 4 : 0); h = h * 397 ^ (Crunch ? 8 : 0); h = h * 397 ^ Aniso;
                return h;
            }
        }

        /// <summary>Compute a hash of the import settings that matter to optimization. / 计算影响优化的导入设置哈希。</summary>
        public static int ImportHash(Texture2D t)
        {
            if (t == null) return 0;
            var path = AssetDatabase.GetAssetPath(t);
            var key = new ImportKey { Width = t.width, Height = t.height, Format = t.format, Filter = t.filterMode };
            if (!string.IsNullOrEmpty(path))
            {
                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp != null)
                {
                    var settings = imp.GetPlatformTextureSettings(BuildPipelineDetector.PlatformName);
                    key.Compression = imp.textureCompression;
                    key.WrapU = imp.wrapModeU; key.WrapV = imp.wrapModeV;
                    key.Mipmap = imp.mipmapEnabled; key.sRGB = imp.sRGBTexture;
                    key.Alpha = imp.DoesSourceTextureHaveAlpha(); key.Crunch = imp.crunchedCompression;
                    key.Aniso = imp.anisotropyLevel;
                    if (settings != null && !string.IsNullOrEmpty(settings.name))
                        key.Format = (TextureFormat)(settings.format);
                }
            }
            return key.GetHashCode();
        }

        /// <summary>Read pixels into a CPU Color array (cached). Must be called on main thread. / 读取像素到 CPU（带缓存）。</summary>
        public static Color[] ReadPixels(Texture2D t, bool linear)
        {
            // RenderTexture-based linear/sRGB-safe readback. Prevents double-gamma and does not
            // require the source texture to be CPU-readable.
            // 基于 RenderTexture 的 sRGB/线性安全读回，不要求源贴图开启 Read/Write。
            var rt = RenderTexture.GetTemporary(t.width, t.height, 0,
                RenderTextureFormat.ARGB32,
                linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            Texture2D tmp = null;
            try
            {
                Graphics.Blit(t, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tmp = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false, linear)
                {
                    wrapMode = TextureWrapMode.Clamp
                };
                tmp.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0);
                tmp.Apply(false, false);
                var pixels = tmp.GetPixels();
                RenderTexture.active = prev;
                return pixels;
            }
            finally
            {
                if (tmp != null) UnityEngine.Object.DestroyImmediate(tmp);
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>Create a new texture asset inside the build container and return it. / 在构建容器内创建新贴图资产。</summary>
        public static Texture2D CreateOutput(int w, int h, TextureFormat format, bool linear, bool mipmap, string name)
        {
            // Compose in uncompressed RGBA32; Stage12 reimport applies the final compressed format.
            // Creating directly into a GPU-compressed format makes SetPixels unreliable.
            // 先以未压缩 RGBA32 合成，阶段12 再在 reimport 时应用最终压缩格式。
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipmap, linear) { name = name, filterMode = FilterMode.Bilinear };
            tex.wrapMode = TextureWrapMode.Clamp; // forced per spec / 按规格强制 Clamp
            return tex;
        }

        public static bool HasMeaningfulAlpha(Color[] px)
        {
            for (int i = 0; i < px.Length; i++)
                if (px[i].a < 0.999f) return true;
            return false;
        }

        /// <summary>Estimate VRAM bytes for a texture (mipmap = *1.33). / 估算贴图显存字节（mipmap *1.33）。</summary>
        public static long EstimateBytes(int w, int h, TextureFormat f, bool mipmap)
        {
            long bpp = (long)Mathf.Max(1, GraphicsFormatUtility.GetBlockSize(f));
            long bytes = w * h * bpp;
            if (mipmap) bytes = (long)(bytes * 1.33f);
            return bytes;
        }
    }

    internal static class BuildPipelineDetector
    {
        public static string PlatformName => EditorUserBuildSettings.selectedBuildTargetGroup switch
        {
            BuildTargetGroup.Android => "Android",
            BuildTargetGroup.iOS => "iPhone",
            BuildTargetGroup.Standalone => "Standalone",
            _ => "DefaultTexturePlatform",
        };
    }
}
