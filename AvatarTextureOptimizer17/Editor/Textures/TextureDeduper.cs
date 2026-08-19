// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Textures/TextureDeduper.cs — 贴图去重 / Texture deduplication
//
// 需求: 处理前先按实际像素和导入设置（若导入设置不同直接视为不同）去重并更新所有相关引用；
//       若去重存在白名单，则去重结果也视为白名单。
// 实现 (共识):
//  - 先按导入设置指纹分组（指纹不同 → 不同，不解码省内存）；组内再按像素哈希去重。
//  - 全局 old→canonical 映射，供材质/动画补丁阶段统一重写引用。
// ============================================================================
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 贴图去重结果 / Texture dedup result.
    /// </summary>
    public sealed class TextureDedupResult
    {
        /// <summary>old → canonical / old texture → canonical texture</summary>
        public Dictionary<Texture2D, Texture2D> map = new Dictionary<Texture2D, Texture2D>();

        /// <summary>被白名单影响的 canonical 集合 / canonicals that became whitelisted due to dedup</summary>
        public HashSet<Texture2D> whitelistedCanonicals = new HashSet<Texture2D>();

        public int RemovedCount => map.Count;
    }

    /// <summary>
    /// 贴图去重器 / Texture deduplicator.
    /// </summary>
    public static class TextureDeduper
    {
        /// <summary>
        /// 执行去重并返回映射 / Run dedup and return the mapping.
        /// </summary>
        /// <param name="textures">全部贴图 / all textures</param>
        /// <param name="isWhitelisted">白名单判定（texture → bool）/ whitelist check</param>
        /// <param name="cache">解码缓存 / decode cache</param>
        public static TextureDedupResult Deduplicate(IEnumerable<Texture2D> textures,
            System.Func<Texture2D, bool> isWhitelisted, TextureDecodeCache cache)
        {
            var result = new TextureDedupResult();

            // 1. 按导入设置指纹分组 / group by import-settings fingerprint
            var byFingerprint = new Dictionary<string, List<Texture2D>>();
            foreach (var tex in textures)
            {
                if (tex == null) continue;
                var fp = Fingerprint(tex);
                if (fp == null) continue; // 无导入设置(运行时纹理等) 不去重 / no importer → skip
                if (!byFingerprint.TryGetValue(fp, out var list))
                {
                    list = new List<Texture2D>();
                    byFingerprint[fp] = list;
                }
                list.Add(tex);
            }

            // 2. 组内像素哈希去重 / hash within group
            using var md5 = MD5.Create();
            foreach (var kv in byFingerprint)
            {
                if (kv.Value.Count < 2) continue;

                var byHash = new Dictionary<string, List<Texture2D>>();
                foreach (var tex in kv.Value)
                {
                    bool srgb = AvatarAnalyzer.GetTextureImporter(tex)?.sRGBTexture ?? false;
                    var pixels = cache.GetRawPixels(tex, srgb);
                    var hash = ComputeHash(md5, pixels);
                    if (!byHash.TryGetValue(hash, out var list))
                    {
                        list = new List<Texture2D>();
                        byHash[hash] = list;
                    }
                    list.Add(tex);
                }

                foreach (var hkv in byHash)
                {
                    if (hkv.Value.Count < 2) continue;
                    var canonical = hkv.Value[0];
                    for (int i = 1; i < hkv.Value.Count; i++)
                    {
                        var old = hkv.Value[i];
                        if (old == canonical) continue;
                        result.map[old] = canonical;
                        if (isWhitelisted(old)) result.whitelistedCanonicals.Add(canonical);
                    }
                }
            }

            return result;
        }

        private static string ComputeHash(MD5 md5, Color32[] pixels)
        {
            var bytes = new byte[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                int o = i * 4;
                bytes[o] = pixels[i].r;
                bytes[o + 1] = pixels[i].g;
                bytes[o + 2] = pixels[i].b;
                bytes[o + 3] = pixels[i].a;
            }
            var h = md5.ComputeHash(bytes);
            var sb = new StringBuilder(32);
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// 导入设置指纹 / Import-settings fingerprint (null if no importer).
        /// </summary>
        internal static string Fingerprint(Texture2D tex)
        {
            var importer = AvatarAnalyzer.GetTextureImporter(tex);
            if (importer == null) return null;
            var sb = new StringBuilder(128);
            sb.Append(importer.sRGBTexture ? "s1" : "s0");
            sb.Append(importer.mipmapEnabled ? "m1" : "m0");
            sb.Append("|f").Append((int)importer.textureFormat);
            sb.Append("|max").Append(importer.maxTextureSize);
            sb.Append("|w").Append((int)importer.wrapMode);
            sb.Append("|flt").Append((int)importer.filterMode);
            sb.Append("|an").Append(importer.anisoLevel);
            sb.Append("|cr").Append(importer.crunchedCompression ? 1 : 0);
            return sb.ToString();
        }
    }
}
