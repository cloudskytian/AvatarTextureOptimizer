// Texture reading + deduplication.
// / 贴图读取与去重。
// Identity: pixel content + import settings. Import settings are fingerprinted from the TextureImporter.
// / 身份 = 像素内容 + 导入设置。导入设置以 TextureImporter 指纹表示。

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.analysis
{
    /// <summary>
    /// Reads texture pixels without touching import settings (GPU readback) and fingerprints textures.
    /// / 通过 GPU 回读读取贴图像素（不修改导入设置），并为贴图生成指纹。
    /// </summary>
    public static class TextureReader
    {
        private static readonly Dictionary<Texture2D, Color32[]> Cache = new Dictionary<Texture2D, Color32[]>();

        /// <summary>Read RGBA32 pixels of any readable/unreadable Texture2D via RenderTexture. / 通过 RenderTexture 读取 RGBA32 像素。</summary>
        public static Color32[] ReadPixels(Texture2D tex)
        {
            if (Cache.TryGetValue(tex, out var cached)) return cached;

            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var data = new Color32[tex.width * tex.height];
            var tmp = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            tmp.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            var raw = tmp.GetRawTextureData<byte>();
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = new Color32(raw[i * 4], raw[i * 4 + 1], raw[i * 4 + 2], raw[i * 4 + 3]);
            }
            UnityEngine.Object.DestroyImmediate(tmp);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            if (Cache.Count < 512) Cache[tex] = data;   // bounded cache / 有界缓存
            return data;
        }

        /// <summary>Clear the pixel cache (call at pipeline end to free memory). / 清空像素缓存（流水线结束时调用释放内存）。</summary>
        public static void ClearCache()
        {
            Cache.Clear();
        }
    }

    /// <summary>
    /// Deduplicates textures by content + import settings. / 按内容 + 导入设置去重贴图。
    /// </summary>
    public static class TextureDeduper
    {
        private readonly Dictionary<string, TexRecord> _byFingerprint = new Dictionary<string, TexRecord>();

        /// <summary>Compute an import-settings fingerprint for a texture. / 计算导入设置指纹。</summary>
        public static string ImportFingerprint(Texture2D tex)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            var sb = new StringBuilder();
            if (imp != null)
            {
                sb.Append(imp.sRGBTexture).Append('|');
                sb.Append(imp.textureType).Append('|');
                sb.Append(imp.alphaIsTransparency).Append('|');
                sb.Append(imp.mipmapEnabled).Append('|');
                sb.Append(imp.alphaSource).Append('|');
                sb.Append(imp.npotScale).Append('|');
                sb.Append(imp.isReadable).Append('|');
                sb.Append(imp.textureCompression).Append('|');
                sb.Append(imp.maxTextureSize);
            }
            sb.Append('|').Append(tex.filterMode).Append('|').Append(tex.wrapMode);
            sb.Append('|').Append(tex.width).Append('x').Append(tex.height);
            return sb.ToString();
        }

        /// <summary>Full fingerprint: content hash + import settings. / 完整指纹：内容哈希 + 导入设置。</summary>
        public static string FullFingerprint(Texture2D tex)
        {
            var px = TextureReader.ReadPixels(tex);
            using (var md5 = MD5.Create())
            {
                var bytes = new byte[px.Length * 4];
                for (int i = 0; i < px.Length; i++)
                {
                    bytes[i * 4] = px[i].r; bytes[i * 4 + 1] = px[i].g; bytes[i * 4 + 2] = px[i].b; bytes[i * 4 + 3] = px[i].a;
                }
                var hash = BitConverter.ToString(md5.ComputeHash(bytes)).Replace("-", "");
                return hash + "|" + ImportFingerprint(tex);
            }
        }

        /// <summary>All records created so far. / 目前已创建的全部记录。</summary>
        public IEnumerable<TexRecord> AllRecords => _byFingerprint.Values;

        /// <summary>Find (or create) the dedup record for a texture. / 查找（或创建）某贴图的去重记录。</summary>
        public TexRecord FindRecord(Texture2D tex) => GetOrCreate(tex);

        /// <summary>Get (or create) the dedup record for a texture. / 获取（或创建）某贴图的去重记录。</summary>
        public TexRecord GetOrCreate(Texture2D tex)
        {
            var fp = FullFingerprint(tex);
            if (_byFingerprint.TryGetValue(fp, out var existing))
            {
                AtoLogVerbose("dedup: " + tex.name + " -> " + existing.Texture.name);
                return existing;
            }
            var record = new TexRecord
            {
                Texture = tex,
                Width = tex.width,
                Height = tex.height,
                HasAlpha = HasAlphaChannel(tex),
                Fingerprint = fp,
            };
            var path = AssetDatabase.GetAssetPath(tex);
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp != null)
            {
                record.IsNormalMap = imp.textureType == TextureImporterType.NormalMap ||
                                     imp.textureType == TextureImporterType.Bump;
                record.IsSrgb = imp.sRGBTexture;
            }
            record.FilterMode = tex.filterMode;
            _byFingerprint[fp] = record;
            return record;
        }

        private static bool HasAlphaChannel(Texture2D tex)
        {
            var px = TextureReader.ReadPixels(tex);
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].a < 255) return true;
            }
            return false;
        }

        private static void AtoLogVerbose(string msg)
        {
            pipeline.AtoLog.VerboseLog(msg);
        }
    }
}
