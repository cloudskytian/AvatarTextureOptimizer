// ImportSettingsUtil.cs - Read & hash TextureImporter settings that affect sampling equality. / 读取并哈希影响采样一致性的贴图导入设置。
using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.ATO.Editor.Analysis
{
    public static class ImportSettingsUtil
    {
        /// <summary>Snapshot of the settings that make two identical pixel textures "different". / 使两张同像素贴图视为“不同”的设置快照。</summary>
        public sealed class Snapshot
        {
            public bool sRGB = true;
            public FilterMode filter = FilterMode.Bilinear;
            public TextureWrapMode wrapU = TextureWrapMode.Repeat, wrapV = TextureWrapMode.Repeat;
            public bool mipmaps = true;
            public bool streamingMipmaps;
            public int aniso = 1;
            public TextureImporterCompression compression = TextureImporterCompression.Compressed;
            public int maxTextureSize = 2048;
            public TextureCompressionQuality crunched;

            public static Snapshot Default => new Snapshot();

            public string Fingerprint()
            {
                var sb = new StringBuilder(128);
                sb.Append(sRGB ? 's' : 'l').Append((int)filter).Append((int)wrapU).Append((int)wrapV)
                  .Append(mipmaps ? 'm' : '_').Append(streamingMipmaps ? 'S' : '_').Append(aniso)
                  .Append((int)compression).Append(maxTextureSize).Append((int)crunched);
                return sb.ToString();
            }
        }

        /// <summary>Snapshot the current-platform import settings of a texture. / 抓取贴图当前平台的导入设置。</summary>
        public static Snapshot Snap(Texture2D tex)
        {
            var s = new Snapshot();
            if (tex == null) return s;
            s.filter = tex.filterMode; s.wrapU = tex.wrapModeU; s.wrapV = tex.wrapModeV;
            s.aniso = tex.anisoLevel; s.mipmaps = tex.mipmapCount > 1;
            s.streamingMipmaps = tex.streamingMipmaps;
            string path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path) && AssetDatabase.GetImporter(path) is TextureImporter imp)
            {
                s.sRGB = imp.sRGBTexture;
                s.compression = imp.textureCompression;
                var ps = imp.GetDefaultPlatformTextureSettings();
                s.maxTextureSize = ps.maxTextureSize;
                var ns = imp.GetPlatformTextureSettings(CurrentPlatformName());
                if (ns.overridden) { s.maxTextureSize = ns.maxTextureSize; s.crunched = (TextureCompressionQuality)ns.textureCompression; }
                if (imp.mipmapEnabled) s.streamingMipmaps = imp.streamingMipmaps;
            }
            else
            {
                // runtime textures: guess by format / 运行时贴图按格式推断
                s.sRGB = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat);
            }
            return s;
        }

        /// <summary>Unity platform texture-setting name for current build target. / 当前构建目标对应的平台设置名。</summary>
        public static string CurrentPlatformName()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return "Android";
                case BuildTarget.iOS: return "iPhone";
                default: return "Standalone";
            }
        }

        /// <summary>Deep content hash: raw bytes + dimensions + format (FNV-1a, two 64-bit lanes).
        /// 深度内容哈希：原始字节+尺寸+格式（FNV-1a双64位通道）。</summary>
        public static Hash128 ContentHash(Texture2D tex)
        {
            if (tex == null) return new Hash128();
            var data = tex.GetRawTextureData<byte>().ToArray();
            ulong h1 = 0xcbf29ce484222325UL, h2 = 0x84222325cbf29ce4UL;
            foreach (var b in data)
            {
                h1 = (h1 ^ b) * 0x100000001b3UL;
                h2 = (h2 ^ (byte)(b * 31 + 7)) * 0x100000001b3UL;
            }
            var meta = new int[3] { tex.width, tex.height, (int)tex.format };
            foreach (var m in meta)
            {
                var lo = (ulong)(uint)m;
                for (int i = 0; i < 4; i++) { h1 = (h1 ^ ((lo >> (i * 8)) & 0xff)) * 0x100000001b3UL; h2 = (h2 ^ ((lo >> (i * 8)) & 0xff)) * 0x100000001b3UL; }
            }
            return Hash128.Parse(h1.ToString("x16") + h2.ToString("x16"));
        }

        /// <summary>Source (pre-import) image size via reflection; falls back to imported size. / 反射读取导入前原图尺寸，失败回退导入尺寸。</summary>
        public static bool TryGetSourceSize(Texture2D tex, out int w, out int h)
        {
            w = h = 0;
            if (tex == null) return false;
            string path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path) && AssetDatabase.GetImporter(path) is TextureImporter imp)
            {
                // TextureImporter.GetSourceTextureWidthAndHeight(object, out int, out int) exists but is internal in some versions / 部分版本为internal，反射调用
                var mi = typeof(TextureImporter).GetMethod("GetSourceTextureWidthAndHeight",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (mi != null)
                {
                    var args = new object[] { null, 0, 0 };
                    bool ok = (bool)mi.Invoke(imp, args);
                    if (ok) { w = (int)args[1]; h = (int)args[2]; if (w > 0 && h > 0) return true; }
                }
            }
            w = tex.width; h = tex.height;
            return false;
        }
    }
}
