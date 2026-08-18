// English: Merge identical opaque material slots when animation never targets a single slot.
// 中文：同一网格上可判定相同的不透明材质，且动画不会单独切其中一个槽时，合并槽并重写动画下标。
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class AtoSlotMerge
    {
        public static void Run(GameObject root, AtoAnimInfo anim)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                if (mats == null || mats.Length < 2) continue;
                if (SlotIndependentlyAnimated(r, anim)) continue;

                var map = new int[mats.Length];
                var unique = new List<Material>();
                var opaqueSame = new Dictionary<int, int>();
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || !IsOpaque(m))
                    {
                        map[i] = unique.Count;
                        unique.Add(m);
                        continue;
                    }
                    int found = -1;
                    for (int u = 0; u < unique.Count; u++)
                        if (unique[u] != null && Same(unique[u], m) && IsOpaque(unique[u]))
                        { found = u; break; }
                    if (found >= 0) { map[i] = found; opaqueSame[i] = found; }
                    else { map[i] = unique.Count; unique.Add(m); }
                }
                if (unique.Count == mats.Length) continue;

                var mesh = r is SkinnedMeshRenderer s ? s.sharedMesh :
                    r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                var clone = Object.Instantiate(mesh);
                clone.name = mesh.name + "_ATOSlots";
                if (!RemapSubmeshes(clone, mesh, map, unique.Count))
                {
                    Object.DestroyImmediate(clone);
                    continue;
                }
                if (r is SkinnedMeshRenderer smr) smr.sharedMesh = clone;
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf) mf.sharedMesh = clone;
                }
                r.sharedMaterials = unique.ToArray();
                RemapAnimSlots(root, r, map);
                AtoLog.Info($"Merged material slots on {r.name}: {mats.Length} → {unique.Count}");
            }
        }

        private static bool SlotIndependentlyAnimated(Renderer r, AtoAnimInfo anim)
        {
            if (anim == null) return false;
            var slots = new HashSet<int>();
            foreach (var s in anim.MaterialSwaps)
                if (s.Renderer == r) slots.Add(s.Slot);
            foreach (var s in anim.TextureSwaps)
                if (s.Renderer == r) slots.Add(s.Slot);
            return slots.Count > 1;
        }

        private static bool IsOpaque(Material m)
        {
            var mode = AtoShaderAnalysis.ReadAlphaMode(m, out _);
            return mode == net.fosa.ato.AtoAlphaMode.Opaque && m.renderQueue < 2450;
        }

        private static bool Same(Material a, Material b)
        {
            if (a == b) return true;
            if (a.shader != b.shader || a.renderQueue != b.renderQueue) return false;
            var pa = a.GetTexturePropertyNames();
            foreach (var p in pa)
                if (a.GetTexture(p) != b.GetTexture(p)) return false;
            return true;
        }

        private static bool RemapSubmeshes(Mesh dst, Mesh src, int[] map, int newCount)
        {
            try
            {
                var combos = new List<CombineInstance>();
                var collected = new List<int>[newCount];
                for (int i = 0; i < newCount; i++) collected[i] = new List<int>();
                for (int sm = 0; sm < src.subMeshCount && sm < map.Length; sm++)
                    collected[map[sm]].AddRange(src.GetTriangles(sm));
                dst.subMeshCount = newCount;
                for (int i = 0; i < newCount; i++)
                    dst.SetTriangles(collected[i], i);
                return true;
            }
            catch (System.Exception e)
            {
                AtoLog.Warn("Slot merge mesh failed: " + e.Message);
                return false;
            }
        }

        private static void RemapAnimSlots(GameObject root, Renderer r, int[] map)
        {
            var clips = new HashSet<AnimationClip>();
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
                if (a.runtimeAnimatorController != null)
                    foreach (var c in a.runtimeAnimatorController.animationClips)
                        if (c) clips.Add(c);
            foreach (var clip in clips)
            {
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var path = b.path ?? "";
                    var tr = string.IsNullOrEmpty(path) ? root.transform : root.transform.Find(path);
                    if (tr == null || tr.GetComponent<Renderer>() != r) continue;
                    var prop = b.propertyName ?? "";
                    int i = prop.IndexOf('[');
                    int j = prop.IndexOf(']');
                    if (i < 0 || j <= i) continue;
                    if (!int.TryParse(prop.Substring(i + 1, j - i - 1), out var slot)) continue;
                    if (slot < 0 || slot >= map.Length) continue;
                    if (map[slot] == slot) continue;
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    var nb = b;
                    nb.propertyName = prop.Substring(0, i + 1) + map[slot] + prop.Substring(j);
                    AnimationUtility.SetObjectReferenceCurve(clip, b, null);
                    AnimationUtility.SetObjectReferenceCurve(clip, nb, keys);
                }
            }
        }
    }
}
