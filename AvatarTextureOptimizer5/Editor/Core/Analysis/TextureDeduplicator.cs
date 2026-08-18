// Copyright (c) fosa. Licensed under the MIT License.
// Content-based texture deduplication. Textures are only merged when their decoded pixels AND
// their import settings match, because differing settings change how the GPU samples them.
// 基于内容的贴图去重。只有当解码像素与导入设置**同时**一致时才合并，
// 因为不同的导入设置会改变 GPU 的采样方式。

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Groups identical textures so downstream stages process each unique image only once.
    /// 将相同贴图分组，使后续阶段对每张唯一图像只处理一次。
    /// </summary>
    public sealed class TextureDeduplicator
    {
        private readonly TextureCache _cache;
        private readonly ATOLogger _log;

        /// <summary>Creates a deduplicator. / 创建去重器。</summary>
        public TextureDeduplicator(TextureCache cache, ATOLogger log)
        {
            _cache = cache;
            _log = log;
        }

        /// <summary>
        /// Maps each duplicate texture to the canonical representative of its group.
        /// Textures with no duplicates map to themselves.
        /// 将每张重复贴图映射到其所属组的规范代表。无重复的贴图映射到自身。
        /// </summary>
        /// <param name="textures">Candidate textures. / 候选贴图。</param>
        /// <param name="whitelisted">
        /// Textures excluded from optimization. If any member of a duplicate group is
        /// whitelisted the representative is whitelisted too, so the exclusion is never lost.
        /// 排除在优化之外的贴图。若重复组中任一成员被列入白名单，
        /// 则代表也被列入白名单，从而不会丢失该排除。
        /// </param>
        public Dictionary<Texture2D, Texture2D> BuildMapping(
            IEnumerable<Texture2D> textures, HashSet<Texture2D> whitelisted)
        {
            var mapping = new Dictionary<Texture2D, Texture2D>();
            var buckets = new Dictionary<string, List<Texture2D>>(StringComparer.Ordinal);

            foreach (var tex in textures)
            {
                if (tex == null) continue;
                if (mapping.ContainsKey(tex)) continue;

                var key = BuildSettingsKey(tex);
                if (!buckets.TryGetValue(key, out var list))
                {
                    list = new List<Texture2D>();
                    buckets[key] = list;
                }

                list.Add(tex);
            }

            var duplicateCount = 0;

            foreach (var bucket in buckets.Values)
            {
                if (bucket.Count == 1)
                {
                    mapping[bucket[0]] = bucket[0];
                    continue;
                }

                // Within a settings bucket, compare actual decoded pixels.
                // 在同一设置桶内，比较实际解码后的像素。
                var representatives = new List<(Texture2D tex, DecodedTexture data)>();

                foreach (var tex in bucket)
                {
                    var data = _cache.Get(tex);
                    if (data == null)
                    {
                        mapping[tex] = tex;
                        continue;
                    }

                    var matched = false;
                    foreach (var (repTex, repData) in representatives)
                    {
                        if (PixelsEqual(data, repData))
                        {
                            mapping[tex] = repTex;
                            matched = true;
                            duplicateCount++;
                            break;
                        }
                    }

                    if (!matched)
                    {
                        representatives.Add((tex, data));
                        mapping[tex] = tex;
                    }
                }
            }

            // Propagate whitelist status across each duplicate group.
            // 在每个重复组内传播白名单状态。
            if (whitelisted != null && whitelisted.Count > 0)
            {
                var extra = new List<Texture2D>();
                foreach (var kv in mapping)
                {
                    if (whitelisted.Contains(kv.Key) && !whitelisted.Contains(kv.Value))
                    {
                        extra.Add(kv.Value);
                    }
                }

                foreach (var t in extra) whitelisted.Add(t);

                // Any texture mapping to a whitelisted representative is itself whitelisted.
                // 任何映射到白名单代表的贴图，其自身也视为白名单。
                foreach (var kv in mapping)
                {
                    if (whitelisted.Contains(kv.Value)) whitelisted.Add(kv.Key);
                }
            }

            if (duplicateCount > 0)
            {
                _log?.Detail($"Texture dedup: {duplicateCount} duplicates merged");
            }

            return mapping;
        }

        /// <summary>
        /// Builds a key from every import setting that affects sampling. Differing settings mean
        /// the textures are NOT interchangeable even when their pixels match.
        /// 依据所有影响采样的导入设置构建键。
        /// 设置不同则贴图不可互换，即使像素完全一致。
        /// </summary>
        public static string BuildSettingsKey(Texture2D tex)
        {
            var sb = new StringBuilder(96);
            sb.Append(tex.width).Append('x').Append(tex.height);
            sb.Append('|').Append(tex.format);
            sb.Append('|').Append(tex.filterMode);
            sb.Append('|').Append(tex.wrapModeU).Append(',').Append(tex.wrapModeV);
            sb.Append('|').Append(tex.anisoLevel);
            sb.Append('|').Append(tex.mipmapCount);
            sb.Append('|').Append(tex.isDataSRGB ? "sRGB" : "linear");

            var path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path) &&
                AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                sb.Append('|').Append(importer.textureType);
                sb.Append('|').Append(importer.alphaSource);
                sb.Append('|').Append(importer.alphaIsTransparency ? 1 : 0);
                sb.Append('|').Append(importer.mipmapEnabled ? 1 : 0);
                sb.Append('|').Append(importer.streamingMipmaps ? 1 : 0);
                sb.Append('|').Append(importer.npotScale);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Exact pixel comparison. An exact test is required: near-identical textures may be
        /// deliberately different assets, and silently merging them would be a visible bug.
        /// 精确像素比较。必须精确：近似相同的贴图可能是有意区分的不同资产，
        /// 静默合并会造成可见的错误。
        /// </summary>
        private static bool PixelsEqual(DecodedTexture a, DecodedTexture b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return false;
            if (a.Pixels == null || b.Pixels == null) return false;
            if (a.Pixels.Length != b.Pixels.Length) return false;

            for (var i = 0; i < a.Pixels.Length; i++)
            {
                var p = a.Pixels[i];
                var q = b.Pixels[i];

                // Compare with a tolerance of well under one 8-bit quantisation step so that
                // float round-trip noise from GPU decoding does not defeat the comparison.
                // 使用远小于 8bit 量化步长的容差比较，
                // 使 GPU 解码的浮点往返噪声不会影响比较结果。
                const float eps = 1f / 2048f;
                if (Mathf.Abs(p.r - q.r) > eps ||
                    Mathf.Abs(p.g - q.g) > eps ||
                    Mathf.Abs(p.b - q.b) > eps ||
                    Mathf.Abs(p.a - q.a) > eps)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
