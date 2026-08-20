// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarTextureOptimizer.Editor.Packing
{
    /// <summary>
    /// Merges duplicate material slots on a renderer. When deduplication leaves a mesh
    /// with several slots pointing at the same (opaque) material, those submeshes can be
    /// combined into one submesh and the extra slots removed — only when no animation
    /// individually switches one of those slots.
    ///
    /// 合并渲染器上的重复材质槽。当去重后某网格有多个槽指向同一（不透明）材质时，可将
    /// 这些子网格合并为一个并移除多余槽 —— 仅当动画没有单独切换其中某个槽时。
    /// </summary>
    public static class ATOMaterialSlotMerger
    {
        /// <summary>
        /// Merge duplicate opaque material slots. Returns the merged mesh (or null if
        /// unchanged) and writes the new material array.
        ///
        /// 合并重复的不透明材质槽。返回合并后的网格（未变化则 null），并写入新材质数组。
        /// </summary>
        public static Mesh Merge(Renderer renderer, bool[] slotAnimated)
        {
            var mats = renderer.sharedMaterials;
            if (mats == null || mats.Length <= 1) return null;

            var mesh = GetMesh(renderer);
            if (mesh == null || mesh.subMeshCount != mats.Length) return null;

            // Group slot indices by material. 按材质分组槽索引。
            var groups = new Dictionary<Material, List<int>>();
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                if (slotAnimated != null && i < slotAnimated.Length && slotAnimated[i]) continue; // skip animated slots.
                if (!IsOpaque(mats[i])) continue;

                if (!groups.TryGetValue(mats[i], out var list)) { list = new List<int>(); groups[mats[i]] = list; }
                list.Add(i);
            }

            bool changed = false;
            foreach (var kv in groups)
            {
                if (kv.Value.Count <= 1) continue;
                changed = true;
                break;
            }
            if (!changed) return null;

            // Build merged triangle array + new submesh layout.
            // 构建合并后的三角形数组与新子网格布局。
            var newMats = new List<Material>();
            var newSubmeshTris = new List<int[]>();
            var slotRemap = new int[mats.Length]; // old slot → new slot. 旧槽 → 新槽。
            var mergedInto = new Dictionary<int, int>(); // old slot → representative new slot.

            // Determine representative slot per group. 确定每组代表槽。
            var representative = new Dictionary<Material, int>();
            foreach (var kv in groups)
                representative[kv.Key] = kv.Value[0];

            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) { slotRemap[i] = -1; continue; }
                if (representative.TryGetValue(mats[i], out int rep) && rep != i)
                {
                    mergedInto[i] = rep;
                    slotRemap[i] = -1; // will be assigned later.
                    continue;
                }
                slotRemap[i] = newMats.Count;
                newMats.Add(mats[i]);
                newSubmeshTris.Add(mesh.GetTriangles(i));
            }

            // Append merged triangles to representative submesh. 将合并三角形并入代表子网格。
            foreach (var kv in mergedInto)
            {
                int oldSlot = kv.Key, repSlot = kv.Value;
                int newIdx = slotRemap[repSlot];
                var combined = new List<int>(newSubmeshTris[newIdx]);
                combined.AddRange(mesh.GetTriangles(oldSlot));
                newSubmeshTris[newIdx] = combined.ToArray();
            }

            // Remap old → new for merged slots (they collapse into representative).
            // 合并槽的旧→新重映射（并入代表槽）。
            foreach (var kv in mergedInto)
                slotRemap[kv.Key] = slotRemap[kv.Value];

            // Build the new mesh. 构建新网格。
            var merged = Object.Instantiate(mesh);
            merged.name = mesh.name + "_ATOSlots";
            merged.subMeshCount = newMats.Count;
            for (int i = 0; i < newMats.Count; i++)
                merged.SetTriangles(newSubmeshTris[i], i);

            // Write new material array. 写入新材质数组。
            var matArr = newMats.ToArray();
            if (renderer is SkinnedMeshRenderer s) s.sharedMaterials = matArr;
            else if (renderer is MeshRenderer m) m.sharedMaterials = matArr;

            return merged;
        }

        private static bool IsOpaque(Material m)
        {
            return m.renderQueue < (int)RenderQueue.AlphaTest;
        }

        private static Mesh GetMesh(Renderer r)
        {
            return r is SkinnedMeshRenderer smr ? smr.sharedMesh
                : r is MeshRenderer mr ? mr.GetComponent<MeshFilter>()?.sharedMesh : null;
        }
    }
}
