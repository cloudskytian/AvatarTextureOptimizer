// TextureDeduplicator.cs / TextureDeduplicator.cs
// Deduplicates textures by pixel content + import settings BEFORE analysis.
// Updates all material references to point to the deduplicated texture.
// 分析前根据像素内容+导入设置去重贴图。更新所有材质引用指向去重后的贴图。

using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Core
{
    public static class TextureDeduplicator
    {
        /// <summary>
        /// Deduplicate a list of textures. Returns mapping old->new.
        /// 去重贴图列表。返回old->new映射。
        /// </summary>
        public static Dictionary<Texture2D, Texture2D> Deduplicate(IEnumerable<Texture2D> textures, HashSet<UnityEngine.Object> whitelistObjects)
        {
            var map = new Dictionary<Texture2D, Texture2D>();
            var byHash = new Dictionary<string, Texture2D>();
            foreach (var t in textures)
            {
                if (t == null) continue;
                string hash = ComputeHash(t);
                bool wl = whitelistObjects.Contains(t);
                if (byHash.TryGetValue(hash, out var existing))
                {
                    map[t] = existing;
                    if (wl) whitelistObjects.Add(existing); // whitelist propagates to dedup target
                }
                else
                {
                    byHash[hash] = t;
                    map[t] = t;
                }
            }
            return map;
        }

        /// <summary>
        /// Apply the deduplication map to all shared materials on renderers and animation clips.
        /// 把去重映射应用到渲染器上的所有共享材质和动画片段。
        /// </summary>
        public static void ApplyMap(Dictionary<Texture2D, Texture2D> map, IEnumerable<Renderer> renderers)
        {
            var updatedMats = new HashSet<Material>();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || updatedMats.Contains(m)) continue;
                    updatedMats.Add(m);
                    var texNames = m.GetTexturePropertyNameIDs();
                    foreach (var tid in texNames)
                    {
                        if (m.GetTexture(tid) is Texture2D t && map.TryGetValue(t, out var rep) && rep != t)
                            m.SetTexture(tid, rep);
                    }
                }
            }
        }

        private static string ComputeHash(Texture2D tex)
        {
            var sb = new StringBuilder();
            sb.Append(tex.width).Append('x').Append(tex.height).Append('-');
            sb.Append(tex.format).Append('-');
            sb.Append(tex.isDataSRGB ? "sRGB" : "lin").Append('-');
            sb.Append(tex.mipmapCount).Append('-');
            sb.Append(tex.wrapModeU).Append(tex.wrapModeV).Append(tex.filterMode).Append(tex.anisoLevel);
            try
            {
                var path = AssetDatabase.GetAssetPath(tex);
                if (!string.IsNullOrEmpty(path))
                {
                    var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (ti != null)
                    {
                        var plat = EditorUserBuildSettings.activeBuildTarget;
                        var ps = ti.GetPlatformTextureSettings(plat.ToString());
                        if (ps != null && ps.overridden)
                            sb.Append('-').Append(ps.format).Append(ps.compressionQuality);
                        sb.Append(ti.alphaIsTransparency ? "a" : "").Append(ti.sRGBTexture ? "s" : "");
                    }
                }
            }
            catch { /* ignore */ }

            if (tex.isReadable && tex.width * tex.height <= 1024 * 1024)
            {
                // Small enough: hash pixel data directly / 足够小：直接哈希像素数据
                var pixels = tex.GetPixels32();
                int stride = Mathf.Max(1, pixels.Length / 256);
                var md5 = MD5.Create();
                byte[] data = new byte[(pixels.Length + stride - 1) / stride * 4];
                int idx = 0;
                for (int i = 0; i < pixels.Length; i += stride)
                {
                    data[idx++] = pixels[i].r; data[idx++] = pixels[i].g;
                    data[idx++] = pixels[i].b; data[idx++] = pixels[i].a;
                }
                var h = md5.ComputeHash(data, 0, idx);
                sb.Append('-').Append(System.Convert.ToBase64String(h));
            }
            return sb.ToString();
        }
    }
}
