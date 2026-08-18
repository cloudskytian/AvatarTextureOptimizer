// Avatar Texture Optimizer (ATO)
// Merges identical opaque material slots on a mesh (combining submeshes) and records the
// slot-index remap for animation rewriting. Animated slots are never merged.
// 合并网格上相同的不透明材质槽（合并子网格），并记录槽索引重映射供动画改写。被动画切换的槽绝不合并。

using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Stage 7b: merge identical opaque slots per renderer. / 阶段 7b：逐渲染器合并相同的不透明槽。
    /// </summary>
    public static class ATOMaterialSlotMerger
    {
        public static void Merge(ATOBuildContext build, ATOProgress progress)
        {
            if (!build.profile.mergeOpaqueSlots) return;
            progress.Begin(build.renderers.Count);

            foreach (var rr in build.renderers)
            {
                if (!rr.EffectiveEnabled || rr.workingMesh == null) { progress.Advance(1); continue; }
                MergeRenderer(build, rr);
                progress.Advance(1, rr.renderer.name);
                progress.ThrowIfCancelled();
            }
        }

        private static void MergeRenderer(ATOBuildContext build, ATORendererRef rr)
        {
            var mesh = rr.workingMesh;
            int slotCount = rr.slots.Length;
            if (mesh.subMeshCount != slotCount) return; // safety: only merge when 1:1 / 安全：仅 1:1 时合并

            // Determine which slots are animated (never merge). / 判定哪些槽被动画切换（绝不合并）。
            var animatedSlots = new HashSet<int>();
            foreach (var key in build.anim.materialSwaps.Keys)
                if (key.Item1 == rr.path) animatedSlots.Add(key.Item2);

            // Group identical adjacent-capable slots. / 分组相同且可合并的槽。
            // oldSlot -> newSlot mapping. / 旧槽 -> 新槽映射。
            var oldToNew = new int[slotCount];
            var newSlots = new List<Material>();
            var mergedTris = new List<int[]>(); // triangles per new slot / 每个新槽的三角形

            for (int s = 0; s < slotCount; s++)
            {
                var mat = rr.slots[s];
                // Try to fold this slot into an earlier identical, non-animated, opaque slot. / 尝试并入之前相同且可合并的槽。
                int target = -1;
                if (mat != null && !animatedSlots.Contains(s) && IsOpaque(build, mat))
                {
                    for (int t = 0; t < newSlots.Count; t++)
                    {
                        if (newSlots[t] == mat && !animatedSlots.Contains(IndexOfOld(t, oldToNew)))
                        {
                            target = t;
                            break;
                        }
                    }
                }

                if (target >= 0)
                {
                    // Merge this submesh into the target's triangle list. / 把该子网格并入目标三角形列表。
                    var tris = mesh.GetTriangles(s);
                    var merged = mergedTris[target];
                    var combined = new int[merged.Length + tris.Length];
                    merged.CopyTo(combined, 0);
                    tris.CopyTo(combined, merged.Length);
                    mergedTris[target] = combined;
                    oldToNew[s] = target;
                }
                else
                {
                    oldToNew[s] = newSlots.Count;
                    newSlots.Add(mat);
                    mergedTris.Add(mesh.GetTriangles(s));
                }
            }

            if (newSlots.Count == slotCount) return; // nothing merged / 无需合并

            // Write back submeshes. / 回写子网格。
            mesh.subMeshCount = newSlots.Count;
            for (int i = 0; i < newSlots.Count; i++)
                mesh.SetTriangles(mergedTris[i], i);

            // Update slots and record remap. / 更新槽并记录重映射。
            rr.slots = newSlots.ToArray();
            rr.renderer.sharedMaterials = rr.slots;
            var remap = new Dictionary<int, int>();
            for (int s = 0; s < slotCount; s++) remap[s] = oldToNew[s];
            build.animRemap.slotRemap[rr.rendererId] = remap;

            ATOLogger.Info($"Merged material slots on '{rr.renderer.name}': {slotCount} -> {newSlots.Count}.");
        }

        private static int IndexOfOld(int newIndex, int[] oldToNew)
        {
            for (int i = 0; i < oldToNew.Length; i++)
                if (oldToNew[i] == newIndex) return i;
            return -1;
        }

        private static bool IsOpaque(ATOBuildContext build, Material m)
        {
            if (build.anim.animatedAlpha.ContainsKey(m)) return false; // animated render mode / 渲染模式被动画修改
            return ATOAvatarScanner.ResolveAlphaMode(m) == ATOAlphaMode.Opaque;
        }
    }
}
