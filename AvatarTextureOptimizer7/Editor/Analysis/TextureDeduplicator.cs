using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Dedup by decoded pixels AND importer settings. Different importers stay different.
    /// If any source was whitelist, the kept instance is whitelist too.
    /// 按解码像素 + 导入设置去重。导入设置不同则视为不同。
    /// 任一源在白名单，则去重结果也在白名单。
    /// </summary>
    public static class TextureDeduplicator
    {
        public static Dictionary<Texture2D, Texture2D> Dedup(
            IEnumerable<Texture2D> textures,
            HashSet<Texture2D> whitelist,
            AtoLog log)
        {
            var map = new Dictionary<Texture2D, Texture2D>();
            var groups = new Dictionary<string, Texture2D>();
            var whiteKeys = new HashSet<string>();
            int saved = 0;

            foreach (var tex in textures)
            {
                if (tex == null) continue;
                var key = BuildKey(tex, log);
                if (whitelist.Contains(tex)) whiteKeys.Add(key);
                if (groups.TryGetValue(key, out var keep))
                {
                    if (!ReferenceEquals(keep, tex))
                    {
                        map[tex] = keep;
                        saved++;
                    }
                }
                else
                {
                    groups[key] = tex;
                    map[tex] = tex;
                }
            }

            foreach (var kv in map)
            {
                if (whiteKeys.Contains(BuildKey(kv.Value, null)))
                    whitelist.Add(kv.Value);
                if (whiteKeys.Contains(BuildKey(kv.Key, null)))
                    whitelist.Add(kv.Value);
            }

            log?.Info("Texture content dedup: " + saved + " replaced, " + groups.Count + " unique");
            return map;
        }

        public static string BuildKey(Texture2D tex, AtoLog log)
        {
            if (tex == null) return "null";
            var path = AssetDatabase.GetAssetPath(tex);
            var imp = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as TextureImporter;
            var header = tex.width + "x" + tex.height + "|" + tex.format + "|" + tex.filterMode + "|" +
                         tex.wrapMode + "|" + tex.anisoLevel + "|" + tex.mipmapCount + "|" + tex.isDataSRGB;
            if (imp != null)
            {
                header += "|" + imp.sRGBTexture + "|" + imp.maxTextureSize + "|" + imp.textureCompression + "|" +
                          imp.textureType + "|" + imp.npotScale + "|" + imp.mipmapEnabled + "|" +
                          imp.streamingMipmaps + "|" + imp.filterMode + "|" + imp.wrapMode + "|" +
                          imp.anisoLevel + "|" + imp.alphaIsTransparency + "|" + imp.textureShape;
            }

            try
            {
                var dec = TextureDecodeCache.DecodeNow(tex, false);
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var bytes = new byte[16];
                    for (int i = 0; i < dec.Linear.Length; i += Math.Max(1, dec.Linear.Length / 65536))
                    {
                        var c = dec.Linear[i];
                        Buffer.BlockCopy(BitConverter.GetBytes(c.r), 0, bytes, 0, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(c.g), 0, bytes, 4, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(c.b), 0, bytes, 8, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(c.a), 0, bytes, 12, 4);
                        sha.AppendData(bytes);
                    }

                    // Also mix first/last pixels so tiny maps stay distinct. / 混入首尾像素，避免小岛碰撞。
                    Mix(sha, dec.Linear[0]);
                    Mix(sha, dec.Linear[dec.Linear.Length - 1]);
                    header += "|" + Convert.ToBase64String(sha.GetHashAndReset());
                }
            }
            catch (Exception e)
            {
                log?.VerboseInfo("Dedup hash fallback for " + tex.name + ": " + e.Message);
                header += "|id=" + tex.GetInstanceID();
            }

            return header;
        }

        static void Mix(IncrementalHash sha, Color c)
        {
            var bytes = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(c.r), 0, bytes, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(c.g), 0, bytes, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(c.b), 0, bytes, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(c.a), 0, bytes, 12, 4);
            sha.AppendData(bytes);
        }

        public static void RemapMaterials(IEnumerable<Material> materials, Dictionary<Texture2D, Texture2D> remap, AtoLog log)
        {
            int n = 0;
            foreach (var mat in materials)
            {
                if (mat == null) continue;
                string[] names;
                try { names = mat.GetTexturePropertyNames(); }
                catch { continue; }

                foreach (var prop in names)
                {
                    if (mat.GetTexture(prop) is Texture2D t && remap.TryGetValue(t, out var nt) && nt != null && nt != t)
                    {
                        mat.SetTexture(prop, nt);
                        n++;
                    }
                }
            }

            log?.VerboseInfo("Updated material texture refs after dedup: " + n);
        }
    }
}
