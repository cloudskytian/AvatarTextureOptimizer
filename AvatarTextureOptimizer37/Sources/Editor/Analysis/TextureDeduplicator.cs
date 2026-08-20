// ============================================================================
// ATO - texture dedup (content + import settings)
// ATO - 贴图去重（内容 + 导入设置）
//
// Two textures are equal iff their pixel content AND import settings are
// equal. Content hash = FNV-1a over mip-0 pixels read in strips (memory
// friendly). Import signature = the import fields that actually affect the
// GPU image (format, sRGB, filter, mipmaps, compression, platform
// overrides). Dedup happens BEFORE island extraction; all material +
// animation references are updated at Apply time via the dedup map.
// 当且仅当像素内容与导入设置均相等时两贴图相等。内容哈希 = mip0 像素按条带
// 读取的 FNV-1a（内存友好）。导入签名 = 实际影响 GPU 图像的导入字段。去重发
// 生于岛提取之前；全部材质+动画引用在 Apply 阶段按映射表更新。
// ============================================================================

#region

using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Analysis
{
    public static class TextureDeduplicator
    {
        // FNV-1a 64 FNV-1a 64
        private static ulong Fnv1a(byte[] data, ulong seed)
        {
            ulong hash = seed;
            foreach (var b in data)
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
            return hash;
        }

        /// <summary>Streams mip-0 pixels in strips into FNV-1a. Only the
        /// first strip count (memory budget ~16MB) is hashed for huge
        /// textures, plus the last strip and the center strip as samples.
        /// 按条带流式读取 mip0 像素计算 FNV-1a。超大贴图只哈希头部+中部+尾部
        /// 条带（内存预算约 16MB）。</summary>
        public static string ContentHash(Texture2D tex)
        {
            int w = tex.width, h = tex.height;
            int stripH = 128;
            ulong hash = 0xcbf29ce484222325UL;
            hash = Fnv1a(Encoding.ASCII.GetBytes($"{w}x{h}"), hash);

            var buffer = new byte[Mathf.Max(1, stripH) * w * 4];
            int strips = Mathf.CeilToInt(h / (float) stripH);
            var stripList = new System.Collections.Generic.List<int> { 0 };
            if (strips > 1) stripList.Add(strips / 2);
            if (strips > 1) stripList.Add(strips - 1);

            foreach (int s in stripList.Distinct().OrderBy(x => x))
            {
                int y = s * stripH;
                int ch = Mathf.Min(stripH, h - y);
                var colors = tex.GetPixels(0, y, w, ch);
                // Color32 -> bytes via unsafe-free copy  Color32 -> 字节
                var bytes = new byte[w * ch * 4];
                System.Buffer.BlockCopy(colors, 0, bytes, 0, bytes.Length);
                hash = Fnv1a(bytes, hash);
            }
            return hash.ToString("x16");
        }

        /// <summary>Import settings signature for the current build target.
        /// 当前构建目标的导入设置签名。</summary>
        public static string ImportSignature(Texture2D tex)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return "inmemory";
            if (AssetImporter.GetAtPath(path, out var importer) && importer is TextureImporter ti)
            {
                var p = new StringBuilder();
                p.Append(ti.textureType).Append('|');
                p.Append((int) ti.textureFormat).Append('|');
                p.Append(ti.sRGB).Append('|');
                p.Append((int) ti.filterMode).Append('|');
                p.Append((int) ti.wrapMode).Append('|');
                p.Append(ti.mipMap).Append('|');
                p.Append(ti.npotScale).Append('|');
                p.Append(ti.crunchedCompression).Append('|');
                p.Append(ti.compressionQuality).Append('|');
                // platform overrides of the current build target
                // 当前构建目标的平台覆盖
                var plat = CurrentPlatformSettings(ti);
                if (plat != null)
                {
                    p.Append('|').Append(plat.format).Append(plat.overriden).Append(plat.mipBias);
                }
                return p.ToString();
            }
            return "inmemory";
        }

        private static TextureImporterPlatformSettings CurrentPlatformSettings(TextureImporter ti)
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            switch (target)
            {
                case BuildTarget.Android:
                    return ti.GetPlatformTextureSettings(TextureImporterPlatform.Android);
                case BuildTarget.iOS:
                    return ti.GetPlatformTextureSettings(TextureImporterPlatform.iOS);
                default:
                    return ti.GetPlatformTextureSettings(TextureImporterPlatform.Standalone);
            }
        }
    }
}
