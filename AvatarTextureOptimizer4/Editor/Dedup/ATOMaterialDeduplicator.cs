// Avatar Texture Optimizer (ATO)
// Material dedup by content + parameters (shader, property values, keywords, render queue).
// 按 内容 + 参数（着色器、属性值、关键字、渲染队列）对材质去重。

using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace NetFosa.ATO
{
    /// <summary>
    /// Stage 7a: merge identical materials and update references. / 阶段 7a：合并相同材质并更新引用。
    /// </summary>
    public static class ATOMaterialDeduplicator
    {
        public static void Deduplicate(ATOBuildContext build, ATOProgress progress)
        {
            var groups = new Dictionary<string, List<Material>>();
            var all = CollectMaterials(build);
            progress.Begin(all.Count);

            foreach (var m in all)
            {
                var fp = Fingerprint(m);
                if (!groups.TryGetValue(fp, out var list)) groups[fp] = list = new List<Material>();
                if (!list.Contains(m)) list.Add(m);
                progress.Advance(1);
            }

            int removed = 0;
            foreach (var kvp in groups)
            {
                if (kvp.Value.Count <= 1) continue;
                var canonical = kvp.Value[0];
                for (int i = 1; i < kvp.Value.Count; i++)
                {
                    var dup = kvp.Value[i];
                    build.animRemap.materialRemap[dup] = canonical;
                    removed++;
                }
                // Update renderer slots. / 更新渲染器槽。
                foreach (var rr in build.renderers)
                    for (int s = 0; s < rr.slots.Length; s++)
                        if (rr.slots[s] != null && build.animRemap.materialRemap.TryGetValue(rr.slots[s], out var c))
                            rr.slots[s] = c;
                // Update usages. / 更新使用记录。
                foreach (var tr in build.textures)
                    foreach (var u in tr.usages)
                        if (u.material != null && build.animRemap.materialRemap.TryGetValue(u.material, out var c2))
                            u.material = c2;
            }

            ATOLogger.Info($"Material dedup: removed {removed} duplicate materials.");
        }

        private static List<Material> CollectMaterials(ATOBuildContext build)
        {
            var set = new HashSet<Material>();
            foreach (var rr in build.renderers)
                foreach (var m in rr.slots)
                    if (m != null) set.Add(m);
            foreach (var tr in build.textures)
                foreach (var u in tr.usages)
                    if (u.material != null) set.Add(u.material);
            return new List<Material>(set);
        }

        /// <summary>Fingerprint a material by all observable content. / 按全部可观测内容生成材质指纹。</summary>
        public static string Fingerprint(Material m)
        {
            if (m == null || m.shader == null) return "null";
            var sb = new StringBuilder();
            sb.Append(m.shader.name).Append('|');
            sb.Append(m.renderQueue).Append('|');
            sb.Append(m.globalIlluminationFlags).Append('|');
            sb.Append(m.doubleSidedGI).Append('|');
            sb.Append(m.enableInstancing).Append('|');
            var kw = m.shaderKeywords;
            System.Array.Sort(kw);
            sb.Append(string.Join(",", kw)).Append('|');

            int count = m.shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                var name = m.shader.GetPropertyName(i);
                if (!m.HasProperty(name)) continue;
                sb.Append(name).Append('=');
                switch (m.shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color:
                        sb.Append(m.GetColor(name)).Append(';'); break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        sb.Append(m.GetFloat(name)).Append(';'); break;
                    case ShaderPropertyType.Vector:
                        sb.Append(m.GetVector(name)).Append(';'); break;
                    case ShaderPropertyType.Int:
                        sb.Append(m.GetInt(name)).Append(';'); break;
                    case ShaderPropertyType.Texture:
                        var t = m.GetTexture(name);
                        sb.Append(t != null ? t.GetInstanceID() : 0).Append(';'); break;
                }
            }
            return sb.ToString();
        }
    }
}
