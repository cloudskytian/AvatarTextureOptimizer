// Final dedup: identical generated textures (pages/whole-scaled) merged; identical final
// materials merged; identical opaque slots merged on one mesh when no animation switches
// them individually (submesh merge + animation index shift).
// 最终去重：相同的生成贴图合并；相同的最终材质合并；无动画单独切换时不透明材质槽合并
// （子网格合并 + 动画索引同步更新）。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class FinalDedup
    {
        internal static void Run(AtoSession s)
        {
            using var _ = ATOLog.Scope("FinalDedup");
            int texMerged = 0, matMerged = 0, slotsMerged = 0;

            // ---- textures / 贴图 ----
            if (s.component.dedupTextures)
            {
                var map = new Dictionary<Texture2D, Texture2D>();
                var groups = new Dictionary<string, Texture2D>();
                foreach (var kv in MaterialPatcher.Replacement)
                {
                    var t = kv.Value;
                    if (t == null || !t.isReadable) continue;
                    string key = t.width + "x" + t.height + "|" + (int)t.format + "|" + HashPixels(t);
                    if (groups.TryGetValue(key, out var canonical)) map[t] = canonical;
                    else groups[key] = t;
                }

                if (map.Count > 0)
                {
                    foreach (var ri in s.renderers)
                        foreach (var m in ri.renderer.sharedMaterials)
                            ReplaceInMaterial(m, map);
                    AnimationAnalyzer.ReplaceTextures(s, map);
                    foreach (var k in MaterialPatcher.Replacement.Keys.ToList())
                        if (map.TryGetValue(MaterialPatcher.Replacement[k], out var canon))
                            MaterialPatcher.Replacement[k] = canon;
                    texMerged = map.Count;
                }
            }

            // ---- materials / 材质 ----
            if (s.component.dedupMaterials)
            {
                var map = new Dictionary<Material, Material>();
                var groups = new Dictionary<string, Material>();
                foreach (var ri in s.renderers)
                {
                    var arr = ri.renderer.sharedMaterials;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var m = arr[i];
                        if (m == null) continue;
                        string key = MaterialKey(m);
                        if (groups.TryGetValue(key, out var canonical)) map[m] = canonical;
                        else groups[key] = m;
                    }
                }

                if (map.Count > 0)
                {
                    foreach (var ri in s.renderers)
                    {
                        var arr = ri.renderer.sharedMaterials;
                        bool changed = false;
                        for (int i = 0; i < arr.Length; i++)
                            if (arr[i] != null && map.TryGetValue(arr[i], out var canon) && canon != arr[i])
                            {
                                arr[i] = canon;
                                changed = true;
                            }

                        if (changed) ri.renderer.sharedMaterials = arr;
                    }

                    AnimationAnalyzer.ReplaceMaterials(s, map);
                    matMerged = map.Count;
                }

                // ---- opaque slot merging / 不透明材质槽合并 ----
                foreach (var ri in s.renderers)
                    slotsMerged += MergeSlots(s, ri);
            }

            ATOLog.Info($"final dedup: {texMerged} textures, {matMerged} materials, {slotsMerged} slots merged");
        }

        // ------------------------------------------------------------------
        private static void ReplaceInMaterial(Material m, Dictionary<Texture2D, Texture2D> map)
        {
            if (m == null || m.shader == null) return;
            var shader = m.shader;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                string prop = shader.GetPropertyName(i);
                if (m.GetTexture(prop) is Texture2D t && map.TryGetValue(t, out var nt) && nt != t)
                    m.SetTexture(prop, nt);
            }
        }

        private static string HashPixels(Texture2D t)
        {
            const ulong p = 1099511628211UL;
            ulong h = 14695981039346656037UL;
            var px = t.GetPixels32();
            foreach (var c in px)
            {
                h = (h ^ c.r) * p;
                h = (h ^ c.g) * p;
                h = (h ^ c.b) * p;
                h = (h ^ c.a) * p;
            }
            return h.ToString("X16");
        }

        private static string MaterialKey(Material m)
        {
            // full property walk incl. textures / 全属性（含贴图）
            var sb = new System.Text.StringBuilder(256);
            sb.Append(m.shader != null ? m.shader.name : "null");
            var shader = m.shader;
            if (shader == null) return sb.ToString();
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                var name = shader.GetPropertyName(i);
                sb.Append('|').Append(name).Append('=');
                switch (shader.GetPropertyType(i))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        sb.Append(m.GetColor(name).ToString("F4"));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        sb.Append(m.GetVector(name).ToString("F4"));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        sb.Append(m.GetFloat(name).ToString("R"));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        var t = m.GetTexture(name);
                        sb.Append(t != null ? t.GetInstanceID().ToString() : "-");
                        sb.Append(',').Append(m.GetTextureScale(name).ToString("F4"))
                            .Append(',').Append(m.GetTextureOffset(name).ToString("F4"));
                        break;
                }
            }

            sb.Append("|kw=").Append(m.shaderKeywords.Join(","));
            sb.Append("|rq=").Append(m.renderQueue);
            return sb.ToString();
        }

        private static int MergeSlots(AtoSession s, RendererInfo ri)
        {
            var mats = ri.renderer.sharedMaterials;
            if (mats.Length < 2 || ri.mesh == null) return 0;
            var mesh = ri.mesh;

            // slots animated individually -> no merge / 动画单独切换的槽位不合并
            var animatedSlots = new HashSet<int>();
            if (s.anim.renderers.TryGetValue(ri.path, out var rAnim))
                foreach (var slot in rAnim.slotMaterials.Keys)
                    animatedSlots.Add(slot);

            int merged = 0;
            for (int i = 0; i < mats.Length; i++)
            {
                var a = mats[i];
                if (a == null) continue;
                if (ShaderAnalyzer.DetectAlphaMode(a, out _) != AlphaMode.Opaque) continue;
                if (animatedSlots.Contains(i)) continue;
                if (mesh.GetTopology(i) != MeshTopology.Triangles) continue;

                for (int j = i + 1; j < mats.Length; j++)
                {
                    var b = mats[j];
                    if (b != a) continue;
                    if (animatedSlots.Contains(j)) continue;
                    if (mesh.GetTopology(j) != MeshTopology.Triangles) continue;

                    // merge submesh j into i / 将子网格j并入i
                    var ia = mesh.GetTriangles(i);
                    var ib = mesh.GetTriangles(j);
                    var combined = new int[ia.Length + ib.Length];
                    Array.Copy(ia, 0, combined, 0, ia.Length);
                    Array.Copy(ib, 0, combined, ia.Length, ib.Length);
                    mesh.SetTriangles(combined, i);

                    // rebuild material array without j (keeps submesh indexing) / 重建材质数组
                    var list = mats.ToList();
                    list.RemoveAt(j);
                    mats = list.ToArray();
                    ri.renderer.sharedMaterials = mats;

                    // remove submesh j & shift / 移除子网格j并移动后续
                    for (int k = j; k < mesh.subMeshCount - 1; k++)
                        mesh.SetTriangles(mesh.GetTriangles(k + 1), k);
                    mesh.subMeshCount = mesh.subMeshCount - 1;

                    ShiftAnimationSlots(s, ri.path, j);
                    merged++;
                    j--; // re-check same slot / 原位重查
                }

                // sync slot list length with submesh count / 同步槽位列表
                while (ri.slotMaterials.Count > mats.Length) ri.slotMaterials.RemoveAt(ri.slotMaterials.Count - 1);
            }

            return merged;
        }

        /// <summary>Shift animation m_Materials indices above 'removed' down by one.
        /// 将动画材质索引大于 removed 的下移一位。</summary>
        private static void ShiftAnimationSlots(AtoSession s, string path, int removed)
        {
            foreach (var clip in s.anim.clips)
            {
                foreach (var b in clip.GetObjectCurveBindings().ToList())
                {
                    if (b.path != path) continue;
                    if (!b.propertyName.StartsWith("m_Materials.Array.data[")) continue;
                    int open = b.propertyName.IndexOf('[');
                    int close = b.propertyName.IndexOf(']');
                    if (!int.TryParse(b.propertyName.Substring(open + 1, close - open - 1), out int idx)) continue;
                    if (idx <= removed) continue;

                    var nb = new EditorCurveBinding
                    {
                        path = b.path, type = b.type,
                        propertyName = $"m_Materials.Array.data[{idx - 1}]",
                    };
                    var keys = clip.GetObjectCurve(b);
                    clip.SetObjectCurve(b, null);
                    clip.SetObjectCurve(nb, keys);
                }
            }
        }
    }
}
