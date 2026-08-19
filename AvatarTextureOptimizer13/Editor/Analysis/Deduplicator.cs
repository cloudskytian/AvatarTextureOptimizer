// ATO — Avatar Texture Optimizer
// Deduplication: textures by content + import settings (CLAUDE.md #4/#25), and materials
// by full property equivalence. Whitelist contamination propagates through texture dedup.
// 去重：贴图按"内容 + 导入设置"去重（CLAUDE.md #4/#25），材质按全部属性等价去重。
// 白名单污染经由贴图去重传播。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Deduplication helpers. 去重辅助。
    /// </summary>
    public static class Deduplicator
    {
        /// <summary>
        /// Deduplicate textures by content + import settings and remap all usages.
        /// Returns the canonical texture refs.
        /// 按内容 + 导入设置对贴图去重并重映射全部用途，返回规范化贴图引用。
        /// </summary>
        public static List<ATOTextureRef> DedupTextures(List<ATOTextureRef> textures)
        {
            var byKey = new Dictionary<string, ATOTextureRef>();
            foreach (var texRef in textures)
            {
                if (texRef == null || texRef.texture == null) continue;
                if (!TryBuildKey(texRef.texture, out string key))
                {
                    // Unreadable → whitelist + warning. 不可读 → 白名单 + 警告。
                    texRef.whitelisted = true;
                    ATOLog.Warn(ATOI18n.T(ATOI18nKeys.ErrorNoTextureReadable, texRef.texture.name));
                    continue;
                }
                texRef.dedupKey = key;

                if (byKey.TryGetValue(key, out var canonical))
                {
                    ATOLog.Verbose($"[Dedup] '{texRef.texture.name}' == '{canonical.texture.name}' → merged.");
                    foreach (var u in texRef.usages) u.texture = canonical.texture;
                    canonical.usages.AddRange(texRef.usages);
                    if (texRef.whitelisted) canonical.whitelisted = true;
                }
                else
                {
                    byKey[key] = texRef;
                }
            }
            return byKey.Values.ToList();
        }

        /// <summary>
        /// Build a dedup key from actual pixels + import settings.
        /// 由实际像素 + 导入设置构建去重键。
        /// </summary>
        public static bool TryBuildKey(Texture2D tex, out string key)
        {
            key = null;
            if (tex == null) return false;
            if (!ATOTextureIO.TryReadPixels(tex, out var rgba)) return false;

            var importer = ATOTextureIO.GetImporter(tex);
            string settings;
            if (importer != null)
            {
                settings = $"{importer.sRGBTexture}|{importer.wrapModeU}|{importer.wrapModeV}|{importer.filterMode}|{importer.mipmapEnabled}|{importer.textureType}|{importer.textureCompression}";
            }
            else
            {
                settings = $"{tex.wrapModeU}|{tex.wrapModeV}|{tex.filterMode}";
            }

            ulong hash = Fnv1a(rgba);
            key = $"{tex.width}x{tex.height}|{settings}|{hash:x16}";
            return true;
        }

        /// <summary>FNV-1a 64-bit hash over pixel bytes. 像素字节的 FNV-1a 64 位哈希。</summary>
        public static ulong Fnv1a(Color32[] pixels)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            foreach (var c in pixels)
            {
                hash ^= c.r; hash *= prime;
                hash ^= c.g; hash *= prime;
                hash ^= c.b; hash *= prime;
                hash ^= c.a; hash *= prime;
            }
            return hash;
        }

        /// <summary>
        /// True when two materials are fully equivalent (shader, keywords, render queue, all properties).
        /// 两个材质是否完全等价（着色器、关键字、渲染队列、全部属性）。
        /// </summary>
        public static bool MaterialsEquivalent(Material a, Material b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.shader != b.shader) return false;
            if (a.renderQueue != b.renderQueue) return false;
            if (a.enableInstancing != b.enableInstancing) return false;
            if (a.doubleSidedGI != b.doubleSidedGI) return false;

            var ka = GetKeywordSignature(a);
            var kb = GetKeywordSignature(b);
            if (ka != kb) return false;

            var shader = a.shader;
            int count = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < count; i++)
            {
                string name = ShaderUtil.GetPropertyName(shader, i);
                var type = ShaderUtil.GetPropertyType(shader, i);
                switch (type)
                {
                    case ShaderUtil.ShaderPropertyType.Color:
                        if (a.GetColor(name) != b.GetColor(name)) return false;
                        break;
                    case ShaderUtil.ShaderPropertyType.Vector:
                        if (a.GetVector(name) != b.GetVector(name)) return false;
                        break;
                    case ShaderUtil.ShaderPropertyType.Float:
                    case ShaderUtil.ShaderPropertyType.Range:
                        if (Mathf.Abs(a.GetFloat(name) - b.GetFloat(name)) > 1e-6f) return false;
                        break;
                    case ShaderUtil.ShaderPropertyType.TexEnv:
                        if (a.GetTexture(name) != b.GetTexture(name)) return false;
                        if (!ATOTextureIO.HasNonIdentitySTSafe(a, name, b)) return false;
                        break;
                }
            }
            return true;
        }

        private static string GetKeywordSignature(Material m)
        {
            var kws = new List<string>(m.shaderKeywords ?? Array.Empty<string>());
            foreach (var k in m.enabledKeywords)
            {
                string name = k.name;
                if (!kws.Contains(name)) kws.Add(name);
            }
            kws.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (var k in kws) sb.Append(k).Append(';');
            return sb.ToString();
        }
    }
}
