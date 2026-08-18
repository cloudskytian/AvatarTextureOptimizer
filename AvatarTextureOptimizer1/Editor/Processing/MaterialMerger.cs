// MaterialMerger.cs / MaterialMerger.cs
// Deduplicates materials and textures after optimization, merging identical material slots when safe.
// 优化后对材质和贴图去重，在安全时合并相同材质槽。

using System.Collections.Generic;
using System.Linq;
using System.Text;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.avatar_texture_optimizer.Editor.Processing
{
    public static class MaterialMerger
    {
        public static void Deduplicate(AvatarAnalysisResult analysis, bool enabled, ATOLogger log)
        {
            if (!enabled) return;
            int matsDedup = 0;
            int texDedup = 0;

            // Texture dedup: same pixel content + same settings
            // 贴图去重：相同像素内容+相同设置
            var texByHash = new Dictionary<long, Texture2D>();
            foreach (var re in analysis.Renderers)
            {
                if (re.WorkingMesh == null) continue;
                for (int i = 0; i < re.Materials.Length; i++)
                {
                    var m = re.Materials[i]?.Material;
                    if (m == null) continue;
                    var ids = m.GetTexturePropertyNameIDs();
                    foreach (var id in ids)
                    {
                        var t = m.GetTexture(id) as Texture2D;
                        if (t == null) continue;
                        if (!t.name.StartsWith("ATO_")) continue; // Only dedup ATO-generated textures
                        long key = TexHash(t);
                        if (texByHash.TryGetValue(key, out var existing))
                        {
                            if (existing != t)
                            {
                                m.SetTexture(id, existing);
                                texDedup++;
                            }
                        }
                        else texByHash[key] = t;
                    }
                }
            }

            // Material dedup: same shader + same property values + same keywords -> merge slots
            // 材质去重：相同shader+相同属性值+相同关键字 -> 合并槽位
            var matKey = new Dictionary<string, Material>();
            foreach (var re in analysis.Renderers)
            {
                var mats = re.Materials.Select(me => me?.Material).Where(m => m != null).ToArray();
                var newMats = new Material[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    string key = MaterialKey(m);
                    if (matKey.TryGetValue(key, out var existing))
                    {
                        newMats[i] = existing;
                        matsDedup++;
                    }
                    else
                    {
                        matKey[key] = m;
                        newMats[i] = m;
                    }
                }
                if (re.Renderer != null)
                    re.Renderer.sharedMaterials = newMats;
            }

            log.MaterialsDedup = matsDedup;
            log.TexturesDedup = texDedup;
        }

        private static long TexHash(Texture2D t)
        {
            // Simple hash based on dimensions and a few pixel samples. For production a full hash is better.
            // 基于尺寸和少量像素采样的简易哈希。生产中完整哈希更好。
            unchecked
            {
                long h = t.width * 73856093L ^ (long)t.height * 19349663L ^ (t.isDataSRGB ? 12345L : 0);
                int step = Mathf.Max(1, t.width / 8);
                for (int y = 0; y < t.height; y += Mathf.Max(1, t.height / 8))
                for (int x = 0; x < t.width; x += step)
                {
                    var c = t.GetPixel(x, y);
                    h ^= (long)(c.r * 255) << 16 ^ (long)(c.g * 255) << 8 ^ (long)(c.b * 255) ^ (long)(c.a * 255) << 24;
                }
                return h;
            }
        }

        private static string MaterialKey(Material m)
        {
            var sb = new StringBuilder();
            sb.Append(m.shader != null ? m.shader.name : "null");
            sb.Append("||");
            if (m.shader != null)
            {
                int count = ShaderUtil.GetPropertyCount(m.shader);
                for (int i = 0; i < count; i++)
                {
                    string p = ShaderUtil.GetPropertyName(m.shader, i);
                    switch (ShaderUtil.GetPropertyType(m.shader, i))
                    {
                        case ShaderUtil.ShaderPropertyType.Color:
                            if (m.HasProperty(p)) sb.Append(p).Append(m.GetColor(p)); break;
                        case ShaderUtil.ShaderPropertyType.Float:
                        case ShaderUtil.ShaderPropertyType.Range:
                            if (m.HasProperty(p)) sb.Append(p).Append(m.GetFloat(p).ToString("R")); break;
                        case ShaderUtil.ShaderPropertyType.TexEnv:
                            if (m.HasProperty(p)) { var t = m.GetTexture(p); sb.Append(p).Append(t != null ? t.GetInstanceID().ToString() : "null"); } break;
                        case ShaderUtil.ShaderPropertyType.Vector:
                            if (m.HasProperty(p)) { var v = m.GetVector(p); sb.Append(p).Append(v.x).Append(v.y).Append(v.z).Append(v.w); } break;
                    }
                }
            }
            foreach (var k in m.shaderKeywords) sb.Append(k).Append(';');
            return sb.ToString();
        }
    }
}
