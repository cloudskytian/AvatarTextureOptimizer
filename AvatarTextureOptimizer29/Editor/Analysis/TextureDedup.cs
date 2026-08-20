// Texture dedup by (pixels + import settings) with reference updating.
// 贴图去重：像素内容 + 导入设置双哈希，并更新全部引用。
//
// Spec: dedup happens BEFORE analysis; duplicates with different import settings are
// considered different textures; if any member of a group is whitelisted, the canonical
// result is whitelisted too. / 去重先于分析；导入设置不同视为不同；组内存在白名单则结果视为白名单。

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class TextureDedup
    {
        /// <summary>Compute dedup mapping over all textures referenced by scanned renderers.
        /// 计算去重映射（覆盖扫描到的渲染器引用的全部贴图）。</summary>
        internal static void Run(AtoSession s, HashSet<Texture2D> whitelistTextures)
        {
            if (!s.component.dedupTextures)
            {
                ATOLog.Info("texture dedup disabled by user");
                return;
            }

            using var _ = ATOLog.Scope("TextureDedup");

            var groups = new Dictionary<string, List<Texture2D>>();
            int deduped = 0;

            foreach (var kv in s.texInfos)
            {
                var tex = kv.Key;
                if (s.textureDedupMap.ContainsKey(tex)) continue;

                var cp = TexturePixels.Get(tex, kv.Value.uses.Exists(u => u.kind == TexKind.Normal));
                if (cp == null) continue;

                string key = ImportIdentity(tex) + "|" + ContentHash(cp);
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<Texture2D>();
                list.Add(tex);
            }

            foreach (var list in groups.Values)
            {
                if (list.Count < 2) continue;
                // canonical = first non-whitelisted, else first / 规范实例：优先非白名单，否则第一个
                Texture2D canonical = list.Find(t => !whitelistTextures.Contains(t)) ?? list[0];
                foreach (var t in list)
                {
                    if (t == canonical) continue;
                    s.textureDedupMap[t] = canonical;
                    deduped++;
                    if (whitelistTextures.Contains(t) && !whitelistTextures.Contains(canonical))
                        whitelistTextures.Add(canonical); // spec: dedup keeps whitelist / 白名单传染
                }
            }

            ATOLog.Info($"texture dedup: {deduped} duplicate textures merged");
        }

        /// <summary>Import-settings identity. / 导入设置身份。</summary>
        internal static string ImportIdentity(Texture2D tex)
        {
            var sb = new StringBuilder(128);
            sb.Append(tex.width).Append('x').Append(tex.height);
            sb.Append("|fmt=").Append((int)tex.format);
            sb.Append("|mips=").Append(tex.mipmapCount);
            sb.Append("|filter=").Append((int)tex.filterMode);
            sb.Append("|wrap=").Append((int)tex.wrapModeU).Append(',').Append((int)tex.wrapModeV).Append(',').Append((int)tex.wrapModeW);
            sb.Append("|aniso=").Append(tex.anisoLevel);

            string path = UnityEditor.AssetDatabase.GetAssetPath(tex);
            sb.Append("|srgb=").Append(TexturePixels.IsSrgb(tex, false));
            if (!string.IsNullOrEmpty(path) &&
                UnityEditor.AssetImporter.GetAtPath(path) is UnityEditor.TextureImporter ti)
            {
                sb.Append("|itype=").Append((int)ti.textureType);
                sb.Append("|imip=").Append(ti.mipmapEnabled);
                sb.Append("|istream=").Append(ti.streamingMipmaps);
                sb.Append("|icomp").Append((int)ti.textureCompression);
                sb.Append("|npot=").Append((int)ti.npotScale);
            }
            else sb.Append("|rt");

            return sb.ToString();
        }

        /// <summary>FNV-1a 64 over raw pixels. / 像素 FNV-1a 64 哈希。</summary>
        internal static string ContentHash(CachedPixels cp)
        {
            const ulong p = 1099511628211UL;
            ulong h = 14695981039346656037UL;
            var px = cp.pixels;
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                h = (h ^ c.r) * p;
                h = (h ^ c.g) * p;
                h = (h ^ c.b) * p;
                h = (h ^ c.a) * p;
            }
            return h.ToString("X16");
        }
    }
}
