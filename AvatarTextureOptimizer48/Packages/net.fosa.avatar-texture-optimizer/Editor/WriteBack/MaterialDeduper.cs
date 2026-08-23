// Material deduplication by content signature. / 按内容签名对材质去重。
// Two materials are identical when shader + all properties + keywords + render state match.
// / 两个材质相同当且仅当着色器 + 全部属性 + 关键字 + 渲染状态一致。

using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.writeback
{
    /// <summary>
    /// Deduplicates materials. / 材质去重。
    /// </summary>
    public static class MaterialDeduper
    {
        /// <summary>Compute a content signature for a material. / 计算材质内容签名。</summary>
        public static string Signature(Material m)
        {
            var sb = new StringBuilder();
            sb.Append(m.shader.name).Append('|');
            var props = m.shader.GetPropertyCount();
            for (int i = 0; i < props; i++)
            {
                var name = m.shader.GetPropertyName(i);
                if (!m.HasProperty(name)) continue;
                switch (m.shader.GetPropertyType(i))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        var v = m.GetVector(name);
                        sb.Append(name).Append('=').Append(v.x).Append(',').Append(v.y).Append(',').Append(v.z).Append(',').Append(v.w).Append('|');
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        sb.Append(name).Append('=').Append(m.GetFloat(name)).Append('|');
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        var t = m.GetTexture(name);
                        sb.Append(name).Append('=').Append(t != null ? t.GetInstanceID().ToString() : "null").Append('|');
                        break;
                }
            }
            sb.Append("renderQueue=").Append(m.renderQueue).Append('|');
            var keywords = m.shaderKeywords;
            if (keywords != null)
            {
                foreach (var k in keywords) sb.Append("kw:").Append(k).Append('|');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Deduplicate the given materials in place, returning a map old->representative.
        /// / 就地去重材质，返回 旧->代表 的映射。
        /// </summary>
        public static Dictionary<Material, Material> Deduplicate(IEnumerable<Material> materials)
        {
            var map = new Dictionary<Material, Material>();
            var bySig = new Dictionary<string, Material>();
            foreach (var m in materials)
            {
                if (m == null) continue;
                if (map.ContainsKey(m)) continue;
                string sig = Signature(m);
                if (bySig.TryGetValue(sig, out var rep) && rep != m)
                {
                    map[m] = rep;
                }
                else
                {
                    bySig[sig] = m;
                    map[m] = m;
                }
            }
            return map;
        }
    }
}
