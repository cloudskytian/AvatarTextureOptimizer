using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Material & atlas dedup: identical materials (content + parameters) and identical generated atlases
// are merged and references updated; opaque identical materials on adjacent slots of the same renderer
// are slot-merged with animation index remapping.
// 材质与图集去重：内容与参数完全相同的材质、像素完全相同的生成图集合并并更新引用；
// 同一渲染器相邻槽位上完全相同的不透明材质合并材质槽并重映射动画索引。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class MaterialDeduper
    {
        public static void Dedup(GameObject root, ReferenceUpdater refs, AnimationAnalysis anim, ATOCancellation cancel)
        {
            // ---- Materials. 材质。----
            var sigToMats = new Dictionary<string, List<Material>>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null) continue;
                    string sig = Signature(mat);
                    if (!sigToMats.TryGetValue(sig, out var list)) { list = new List<Material>(); sigToMats[sig] = list; }
                    if (!list.Contains(mat)) list.Add(mat);
                }
            }
            var matMap = new Dictionary<Material, Material>();
            foreach (var kv in sigToMats)
            {
                if (kv.Value.Count <= 1) continue;
                kv.Value.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
                var canonical = kv.Value[0];
                for (int i = 1; i < kv.Value.Count; i++)
                {
                    if (kv.Value[i] != canonical && !matMap.ContainsKey(kv.Value[i])) matMap[kv.Value[i]] = canonical;
                }
            }

            if (matMap.Count > 0)
            {
                foreach (var kv in matMap)
                    ATOLog.Info($"material dedup: '{kv.Key.name}' -> '{kv.Value.name}'");
                // Replace in renderers. 替换渲染器引用。
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = renderer.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                        if (mats[i] != null && matMap.TryGetValue(mats[i], out var c)) { mats[i] = c; changed = true; }
                    if (changed) renderer.sharedMaterials = mats;
                }
                // Replace in animation object-ref curves. 替换动画对象引用曲线。
                foreach (var clip in AnimationAnalyzer.CollectClips(root))
                    refs.RewriteClip(clip, obj => obj is Material m && matMap.TryGetValue(m, out var c) ? c : obj, root);
            }

            // ---- Slot merge (adjacent identical opaque materials, no animation on those slots). 槽位合并。----
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = renderer.sharedMaterials;
                if (mats.Length <= 1) continue;
                string path = AnimationAnalysis.AbsPath(renderer.transform, root);
                var keep = new List<Material> { mats[0] };
                var slotRemap = new int[mats.Length];
                slotRemap[0] = 0;
                for (int i = 1; i < mats.Length; i++)
                {
                    bool prevAnimated = SlotAnimated(path, i - 1, anim, root);
                    bool curAnimated = SlotAnimated(path, i, anim, root);
                    bool merged = mats[i] == keep[keep.Count - 1] && IsOpaque(mats[i]) && !prevAnimated && !curAnimated;
                    if (merged) slotRemap[i] = slotRemap[i - 1];
                    else { keep.Add(mats[i]); slotRemap[i] = keep.Count - 1; }
                }
                bool anyMerge = false;
                for (int i = 0; i < mats.Length; i++) if (slotRemap[i] != i) { anyMerge = true; break; }
                Mesh src = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                // A material array shrink requires a matching submesh merge; otherwise skip the whole merge.
                // 材质数组收缩需要同步合并子网格；计数不匹配时跳过整个合并（避免渲染器损坏）。
                if (anyMerge && (src == null || src.subMeshCount != mats.Length))
                {
                    ATOLog.Warn($"slot merge skipped on {renderer.name}: mesh submesh count ({src?.subMeshCount}) != material count ({mats.Length})");
                    continue;
                }
                if (anyMerge)
                {
                    ATOLog.Info($"slot merge on {renderer.name}: {mats.Length} -> {keep.Count} slots (mesh submeshes merged)");
                    var clone = UnityEngine.Object.Instantiate(src);
                    clone.name = "ATO_" + src.name + "_merged";
                    clone.subMeshCount = keep.Count;
                    for (int s = 0; s < keep.Count; s++)
                    {
                        var tris = new List<int>();
                        for (int old = 0; old < mats.Length; old++)
                            if (slotRemap[old] == s) tris.AddRange(src.GetTriangles(old));
                        clone.SetTriangles(tris, s);
                    }
                    if (renderer is SkinnedMeshRenderer s2) s2.sharedMesh = clone;
                    else renderer.GetComponent<MeshFilter>().sharedMesh = clone;
                    renderer.sharedMaterials = keep.ToArray();
                    refs.RemapSlotIndices(root, (p, oldSlot) =>
                        p == path && oldSlot < slotRemap.Length ? slotRemap[oldSlot] : oldSlot);
                }
            }
        }

        private static bool SlotAnimated(string path, int slot, AnimationAnalysis anim, GameObject root)
        {
            if (anim == null) return false;
            return anim.TryGet(path, $"m_Materials.Array.data[{slot}]._MainTex", out _)
                || anim.TryGet(path, $"m_Materials.Array.data[{slot}]._Cutoff", out _);
        }

        private static bool IsOpaque(Material mat)
        {
            if (mat == null) return false;
            return TextureUseCollector.ResolveAlphaMode(mat) == AlphaMode.Opaque;
        }

        private static string Signature(Material mat)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(mat.shader.name).Append('|');
            foreach (var kw in mat.enabledKeywords) sb.Append(kw).Append(',');
            sb.Append('|');
            int count = ShaderUtil.GetPropertyCount(mat.shader);
            for (int i = 0; i < count; i++)
            {
                var name = ShaderUtil.GetPropertyName(mat.shader, i);
                var type = ShaderUtil.GetPropertyType(mat.shader, i);
                if (!mat.HasProperty(name)) continue;
                switch (type)
                {
                    case ShaderUtil.ShaderPropertyType.Color: sb.Append(name).Append('=').Append(mat.GetColor(name).ToString("F6")).Append(';'); break;
                    case ShaderUtil.ShaderPropertyType.Float:
                    case ShaderUtil.ShaderPropertyType.Range: sb.Append(name).Append('=').Append(mat.GetFloat(name).ToString("F6")).Append(';'); break;
                    case ShaderUtil.ShaderPropertyType.Int: sb.Append(name).Append('=').Append(mat.GetInt(name)).Append(';'); break;
                    case ShaderUtil.ShaderPropertyType.Vector: sb.Append(name).Append('=').Append(mat.GetVector(name).ToString("F6")).Append(';'); break;
                    case ShaderUtil.ShaderPropertyType.TexEnv:
                        var tex = mat.GetTexture(name);
                        sb.Append(name).Append('=').Append(tex != null ? tex.GetInstanceID().ToString() : "null").Append(';');
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
