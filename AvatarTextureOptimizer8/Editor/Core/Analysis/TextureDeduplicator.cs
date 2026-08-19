// TextureDeduplicator.cs
// Groups textures by (dimensions + import settings + pixel content) and builds a
// canonical-replacement map. / 按(尺寸+导入设置+像素内容)分组,构建规范替换映射。
// Copyright (c) 2026 fosa. licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Unity.Collections;

namespace net.fosa.ato
{
    internal static class TextureDeduplicator
    {
        /// <summary>
        /// Build texture→canonical map. Only non-whitelisted analyzed textures participate.
        /// / 构建 贴图→规范代表 映射;仅非白名单的已分析贴图参与。
        /// </summary>
        internal static Dictionary<Texture2D, Texture2D> BuildMap(ATOBuildData d)
        {
            var map = new Dictionary<Texture2D, Texture2D>();

            // ---- Stage 1: import-settings + size key / 第一阶段:导入设置+尺寸键 ----
            var groups = new Dictionary<string, List<Texture2D>>();
            foreach (var node in d.TextureNodes.Values.ToList())
            {
                var tex = node.Tex;
                if (tex == null || d.WhitelistedTextures.Contains(tex)) continue;
                var key = ImportKey(tex);
                if (key == null) continue; // not an asset / 非资产
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<Texture2D>();
                list.Add(tex);
            }

            // ---- Stage 2: pixel hash inside each group / 第二阶段:组内像素哈希 ----
            int hashCount = 0;
            foreach (var g in groups.Values)
            {
                if (g.Count < 2) continue;
                var byHash = new Dictionary<ulong, Texture2D>();
                foreach (var tex in g)
                {
                    ulong hash;
                    try { hash = PixelHash(tex); hashCount++; }
                    catch (Exception e)
                    {
                        ATOLog.Warn($"dedup: pixel read failed for '{tex.name}': {e.Message}");
                        continue;
                    }
                    if (byHash.TryGetValue(hash, out var canonical))
                    {
                        if (!map.ContainsKey(tex)) map[tex] = canonical;
                    }
                    else byHash[hash] = tex;
                }
            }

            if (map.Count > 0) ATOLog.Info($"texture dedup: {map.Count} textures will be replaced by identical twins");
            if (hashCount > 0) ATOLog.V($"dedup: hashed {hashCount} textures");
            return map;
        }

        /// <summary>Import settings + size key. / 导入设置+尺寸键。</summary>
        private static string ImportKey(Texture2D tex)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return null;
            if (!(AssetImporter.GetAtPath(path) is TextureImporter imp)) return null;
            // Full serialized importer state + dimensions + actual format. / 完整导入设置+尺寸+实际格式。
            return $"{tex.width}x{tex.height}:{tex.format}:{tex.mipmapCount}:{tex.filterMode}:" +
                   $"{tex.wrapMode}:{EditorJsonUtility.ToJson(imp).GetHashCode():X8}";
        }

        /// <summary>Robust 64-bit content hash over GPU-read pixels. / 基于 GPU 读取像素的 64 位内容哈希。</summary>
        internal static ulong PixelHash(Texture2D tex)
        {
            var pixels = ATOGpu.Instance.Readback(tex);
            return Fnv164(pixels);
        }

        internal static ulong Fnv164(UnityEngine.NativeArray<Color32> data)
        {
            ulong h = 14695981039346656037;
            int n = data.Length;
            // Sample-based hashing bounds cost on huge textures. / 大贴图按步长采样以控制开销。
            int stride = n > 4 * 1024 * 1024 ? 3 : 1;
            for (int i = 0; i < n; i += stride)
            {
                var c = data[i];
                h ^= c.r; h *= 1099511628211;
                h ^= c.g; h *= 1099511628211;
                h ^= c.b; h *= 1099511628211;
                h ^= c.a; h *= 1099511628211;
            }
            return h;
        }
    }
}
