using System;
using System.Collections.Generic;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using NetFosa.AvatarTextureOptimizer.Editor.Utils;
using UnityEditor;
using UnityEngine;
using NetFosa.AvatarTextureOptimizer;

namespace NetFosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// 贴图去重：按实际像素内容 + 导入设置（导入设置不同直接视为不同）去重，并更新所有相关引用。
    /// 若去重存在白名单，则去重结果也视为白名单（whitelist 传染）。
    /// </summary>
    public static class TextureDeduplicator
    {
        public static int Deduplicate(List<TextureInfo> infos, TextureCache cache, ATOLogger logger)
        {
            var byKey = new Dictionary<string, List<TextureInfo>>();
            foreach (var info in infos)
            {
                if (info.texture == null || info.dedupTarget != null) continue;
                string key = $"{info.texture.width}x{info.texture.height}|{(int)info.colorSpace}|{(int)info.filterMode}|{ImportSettingsHash(info.texture)}";
                if (!byKey.TryGetValue(key, out var list))
                {
                    list = new List<TextureInfo>();
                    byKey[key] = list;
                }
                list.Add(info);
            }

            int mergedCount = 0;
            foreach (var kv in byKey)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;

                var canonical = list[0];
                var canonicalPx = cache.GetPixels(canonical.texture, out _, out _);

                for (int i = 1; i < list.Count; i++)
                {
                    var other = list[i];
                    if (other.dedupTarget != null) continue;
                    var px = cache.GetPixels(other.texture, out _, out _);
                    if (px.Length != canonicalPx.Length) continue;
                    if (!PixelsEqual(canonicalPx, px)) continue;

                    // 合并
                    other.dedupTarget = canonical;
                    foreach (var usage in other.usages)
                    {
                        usage.info = canonical;
                        usage.texture = canonical.texture;
                        canonical.usages.Add(usage);
                    }
                    // whitelist 传染（取更严重的级别 = 数值更小）
                    if (other.whitelisted && !canonical.whitelisted)
                    {
                        canonical.whitelisted = true;
                    }
                    if ((int)other.EffectiveWhitelistLevel < (int)canonical.whitelistLevel)
                        canonical.whitelistLevel = other.EffectiveWhitelistLevel;
                    mergedCount++;
                    logger.VerboseLog($"Deduplicated texture '{other.texture.name}' -> '{canonical.texture.name}'");
                }
            }

            if (mergedCount > 0)
                logger.Info($"Texture dedup: merged {mergedCount} duplicate texture(s).");
            return mergedCount;
        }

        private static bool PixelsEqual(Color32[] a, Color32[] b)
        {
            // 快速哈希 + 逐像素比较
            unchecked
            {
                uint ha = 2166136261, hb = 2166136261;
                int stride = Math.Max(1, a.Length / 4096);
                for (int i = 0; i < a.Length; i += stride)
                {
                    var ca = a[i]; var cb = b[i];
                    ha = (ha ^ ca.r) * 16777619; ha = (ha ^ ca.g) * 16777619; ha = (ha ^ ca.b) * 16777619; ha = (ha ^ ca.a) * 16777619;
                    hb = (hb ^ cb.r) * 16777619; hb = (hb ^ cb.g) * 16777619; hb = (hb ^ cb.b) * 16777619; hb = (hb ^ cb.a) * 16777619;
                }
                if (ha != hb) return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                var ca = a[i]; var cb = b[i];
                if (ca.r != cb.r || ca.g != cb.g || ca.b != cb.b || ca.a != cb.a) return false;
            }
            return true;
        }

        private static int ImportSettingsHash(Texture tex)
        {
            unchecked
            {
                int hash = 17;
                var path = AssetDatabase.GetAssetPath(tex);
                if (!string.IsNullOrEmpty(path))
                {
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer != null)
                    {
                        hash = hash * 31 + importer.maxTextureSize;
                        hash = hash * 31 + (int)importer.textureCompression;
                        hash = hash * 31 + (importer.crunchedCompression ? 1 : 0);
                        hash = hash * 31 + (importer.mipmapEnabled ? 1 : 0);
                        hash = hash * 31 + (importer.streamingMipmaps ? 1 : 0);
                        hash = hash * 31 + (importer.sRGBTexture ? 1 : 0);
                        hash = hash * 31 + (importer.alphaIsTransparency ? 1 : 0);
                        hash = hash * 31 + (int)importer.wrapMode;
                        hash = hash * 31 + (int)importer.filterMode;
                        hash = hash * 31 + (int)importer.npotScale;
                        hash = hash * 31 + (int)importer.alphaSource;
                        hash = hash * 31 + (int)importer.textureShape;
                    }
                }
                hash = hash * 31 + (int)tex.format;
                hash = hash * 31 + tex.mipmapCount;
                hash = hash * 31 + tex.width;
                hash = hash * 31 + tex.height;
                return hash;
            }
        }
    }
}
