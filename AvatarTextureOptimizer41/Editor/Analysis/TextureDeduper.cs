using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

// Texture deduplication: textures with identical pixels AND identical import settings are merged.
// Whitelisted members make the merged result whitelisted too.
// 贴图去重：像素与导入设置完全相同的贴图合并；存在白名单成员时合并结果也视为白名单。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class TextureDeduper
    {
        /// <summary>
        /// Returns old->new texture mapping (identity excluded) and merges references in the avatar's
        /// renderer materials (cloning materials when changed — never mutating user assets).
        /// 返回 old→new 贴图映射（不含恒等映射），并更新 Avatar 渲染器材质上的引用（改动时克隆材质，绝不改动用户资产）。
        /// </summary>
        public static Dictionary<Texture2D, Texture2D> Dedup(GameObject root, WhiteListEvaluator white, TextureDecodeCache decode, ReferenceUpdater refs)
        {
            var result = new Dictionary<Texture2D, Texture2D>();
            var byKey = new Dictionary<string, List<Texture2D>>();

            foreach (var tex in CollectAllTextures(root))
            {
                if (tex == null) continue;
                string key = BuildKey(tex, decode);
                if (!byKey.TryGetValue(key, out var list)) { list = new List<Texture2D>(); byKey[key] = list; }
                list.Add(tex);
            }

            var warnings = new List<string>();
            foreach (var kv in byKey)
            {
                var list = kv.Value;
                if (list.Count <= 1) continue;
                // Canonical = the first (lowest instance id) texture. 规范对象 = 第一个（实例 ID 最小）。
                list.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
                var canonical = list[0];
                bool anyWhitelisted = false;
                foreach (var t in list) if (white.IsTextureWhitelisted(t, null)) anyWhitelisted = true;
                if (anyWhitelisted)
                {
                    // Dedup result is treated as whitelisted. 去重结果视为白名单。
                    white.AddWhitelisted(canonical);
                    warnings.Add($"dedup: {string.Join(", ", list.ConvertAll(t => t.name))} merged into {canonical.name}; whitelisted (member was whitelisted)");
                }
                else
                {
                    warnings.Add($"dedup: {string.Join(", ", list.ConvertAll(t => t.name))} merged into {canonical.name}");
                }
                for (int i = 1; i < list.Count; i++)
                {
                    if (!result.ContainsKey(list[i])) result[list[i]] = canonical;
                }
            }

            foreach (var kv in result)
                ATOLog.Info($"texture dedup: '{kv.Key.name}' -> '{kv.Value.name}'");

            refs.RewriteTextures(root, result);
            return result;
        }

        private static List<Texture2D> CollectAllTextures(GameObject root)
        {
            var set = new HashSet<Texture2D>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null || mat.shader == null) continue;
                    foreach (var prop in MaterialUtil.EnumerateTextureProperties(mat))
                    {
                        if (mat.GetTexture(prop) is Texture2D t) set.Add(t);
                    }
                }
            }
            return new List<Texture2D>(set);
        }

        /// <summary>
        /// Fingerprint: MD5 of decoded pixel bytes + import settings string.
        /// 指纹：解码像素字节的 MD5 + 导入设置字符串。
        /// </summary>
        public static string BuildKey(Texture2D tex, TextureDecodeCache decode)
        {
            var sb = new StringBuilder();
            sb.Append(ImportSettingsKey(tex));
            sb.Append('|');
            var raw = decode.Get(tex).RawRGBA;
            byte[] bytes = new byte[raw.Length * 4];
            for (int i = 0; i < raw.Length; i++)
            {
                bytes[i * 4] = raw[i].r; bytes[i * 4 + 1] = raw[i].g; bytes[i * 4 + 2] = raw[i].b; bytes[i * 4 + 3] = raw[i].a;
            }
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(bytes);
                sb.Append(BitConverter.ToString(hash));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Import-settings fingerprint (different import settings => different texture). 
        /// 导入设置指纹（导入设置不同即视为不同贴图）。
        /// </summary>
        public static string ImportSettingsKey(Texture2D tex)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return $"runtime:{tex.width}x{tex.height}:{tex.filterMode}:{tex.wrapMode}";
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return $"nontex:{tex.width}x{tex.height}";

            var sb = new StringBuilder();
            sb.Append(imp.textureType).Append('|');
            sb.Append(imp.sRGBTexture).Append('|');
            sb.Append(imp.alphaIsTransparency).Append('|');
            sb.Append(imp.alphaSource).Append('|');
            sb.Append(imp.mipmapEnabled).Append('|');
            sb.Append(imp.streamingMipmaps).Append('|');
            sb.Append(imp.filterMode).Append('|');
            sb.Append(imp.wrapMode).Append('|');
            sb.Append(imp.maxTextureSize).Append('|');
            sb.Append(imp.textureCompression).Append('|');
            sb.Append(imp.compressionQuality).Append('|');
            sb.Append(imp.crunchCompression).Append('|');
            sb.Append(imp.npotScale).Append('|');
            foreach (var platform in new[] { "Standalone", "Android", "iPhone" })
            {
                var ps = imp.GetPlatformTextureSettings(platform);
                if (ps == null) continue;
                sb.Append($"{platform}:{ps.overridden}:{ps.maxTextureSize}:{ps.format}:{ps.textureCompression}:{ps.crunchCompression};");
            }
            return sb.ToString();
        }
    }
}
