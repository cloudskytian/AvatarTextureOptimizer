// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Sub-mesh / material-slot merging.
// AvatarTextureOptimizer (ATO) - 子网格 / 材质槽合并。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Net.Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Apply
{
    /// <summary>
    /// EN: After atlasing, several material slots of one renderer often end up referencing the exact same
    ///     material. Merging their sub-meshes removes real draw calls, but it also renumbers sub-meshes,
    ///     which anything that addresses material slots by index depends on. We therefore only merge when
    ///     we can prove nothing depends on the old numbering, and we rewrite the animation bindings that we
    ///     do know about.
    /// ZH: 图集化之后，一个渲染器的多个材质槽经常会引用完全相同的材质。
    ///     合并它们的子网格能实打实地减少 Draw Call，但也会让子网格重新编号，
    ///     而任何按索引寻址材质槽的东西都依赖这个编号。因此我们只在能证明没有东西依赖旧编号时才合并，
    ///     并且会重写我们已知的动画绑定。
    /// </summary>
    public static class SubMeshMerger
    {
        /// <summary>
        /// EN: Serialized field names that indicate a component addresses material slots by index. If a
        ///     sibling component exposes one of these we refuse to merge, because we cannot safely renumber
        ///     data we do not understand (AAO's Remove Mesh By UV Tile is the motivating example).
        /// ZH: 表明某组件按索引寻址材质槽的序列化字段名。若同物体上的组件暴露了其中之一，我们拒绝合并，
        ///     因为无法安全地为我们不理解的数据重新编号（AAO 的 Remove Mesh By UV Tile 就是典型例子）。
        /// </summary>
        private static readonly string[] SlotIndexedFieldNames =
        {
            "materials", "materialSettings", "materialSlots", "materialIndices", "slots",
        };

        public sealed class MergeResult
        {
            public int RenderersChanged;
            public int SlotsRemoved;
            public readonly List<string> Skipped = new List<string>();
        }

        /// <summary>
        /// EN: Merge duplicate material slots on every renderer of the avatar.
        /// ZH: 合并 Avatar 上每个渲染器的重复材质槽。
        /// </summary>
        public static MergeResult MergeAll(BuildContext ctx, AnimationFacts facts)
        {
            var result = new MergeResult();

            foreach (var renderer in ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;

                var path = nadena.dev.ndmf.runtime.RuntimeUtil.RelativePath(ctx.AvatarRootObject,
                    renderer.gameObject) ?? "";

                if (!TryMerge(ctx, renderer, path, facts, out int removed, out string reason))
                {
                    if (reason != null) result.Skipped.Add($"{path}: {reason}");
                    continue;
                }

                result.RenderersChanged++;
                result.SlotsRemoved += removed;
            }

            if (result.RenderersChanged > 0)
            {
                ATOLog.Info($"merged material slots on {result.RenderersChanged} renderer(s), " +
                            $"removed {result.SlotsRemoved} slot(s)");
            }
            foreach (var s in result.Skipped) ATOLog.Debug_($"slot merge skipped - {s}");
            return result;
        }

        private static bool TryMerge(BuildContext ctx, Renderer renderer, string path, AnimationFacts facts,
            out int removedSlots, out string reason)
        {
            removedSlots = 0;
            reason = null;

            var materials = renderer.sharedMaterials;
            if (materials.Length < 2) return false;

            // ---- Are there duplicates at all? / 是否真的存在重复？ ----
            var firstIndexOf = new Dictionary<Material, int>();
            var remap = new int[materials.Length];
            var kept = new List<Material>();
            bool anyDuplicate = false;

            for (int i = 0; i < materials.Length; i++)
            {
                var m = materials[i];
                if (m == null) { reason = "null material slot"; return false; }

                if (firstIndexOf.TryGetValue(m, out var first))
                {
                    remap[i] = remap[first];
                    anyDuplicate = true;
                }
                else
                {
                    firstIndexOf[m] = i;
                    remap[i] = kept.Count;
                    kept.Add(m);
                }
            }
            if (!anyDuplicate) return false;

            // ---- Safety gate 1: animation must not drive these slots / 安全门 1：动画不得驱动这些槽 ----
            for (int i = 0; i < materials.Length; i++)
            {
                if (facts.AnimatedMaterialSlots.Contains((path, i)))
                {
                    reason = "material slot is animated";
                    return false;
                }
            }

            // ---- Safety gate 2: no sibling component addresses slots by index ----
            // ---- 安全门 2：同物体上没有按索引寻址材质槽的组件 ----
            if (HasSlotIndexedComponent(renderer, out var offender))
            {
                reason = $"'{offender}' addresses material slots by index";
                return false;
            }

            var mesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh : GetFilterMesh(renderer);
            if (mesh == null) { reason = "no mesh"; return false; }
            if (mesh.subMeshCount != materials.Length)
            {
                // EN: Unity's behaviour when the counts differ is subtle (extra slots re-draw the last
                //     sub-mesh). Refuse rather than guess.
                // ZH: 数量不一致时 Unity 的行为很微妙（多余的槽会重绘最后一个子网格）。不猜，直接拒绝。
                reason = $"sub-mesh count {mesh.subMeshCount} != material count {materials.Length}";
                return false;
            }

            // ---- Safety gate 3: triangles only / 安全门 3：仅限三角形拓扑 ----
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                if (mesh.GetTopology(i) != MeshTopology.Triangles)
                {
                    reason = $"sub-mesh {i} is not a triangle list";
                    return false;
                }
            }

            // ---- Rebuild the index buffer / 重建索引缓冲 ----
            var working = ctx.IsTemporaryAsset(mesh) ? mesh : UnityEngine.Object.Instantiate(mesh);
            if (!ReferenceEquals(working, mesh))
            {
                working.name = mesh.name + "_ATOMerged";
                ctx.AssetSaver.SaveAsset(working);
            }

            var mergedTriangles = new List<int>[kept.Count];
            for (int i = 0; i < kept.Count; i++) mergedTriangles[i] = new List<int>();

            for (int i = 0; i < materials.Length; i++)
            {
                mergedTriangles[remap[i]].AddRange(working.GetTriangles(i));
            }

            working.subMeshCount = kept.Count;
            for (int i = 0; i < kept.Count; i++)
            {
                working.SetTriangles(mergedTriangles[i], i, calculateBounds: false);
            }
            working.RecalculateBounds();

            if (renderer is SkinnedMeshRenderer skinned) skinned.sharedMesh = working;
            else
            {
                var mf = renderer.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = working;
            }

            removedSlots = materials.Length - kept.Count;
            renderer.sharedMaterials = kept.ToArray();

            // ---- Rewrite the animation bindings we own / 重写我们能掌握的动画绑定 ----
            RewriteMaterialArrayBindings(ctx, path, remap, materials.Length, kept.Count);

            ATOLog.Debug_($"'{path}': merged {materials.Length} slot(s) into {kept.Count}");
            return true;
        }

        private static Mesh GetFilterMesh(Renderer r)
        {
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        private static bool HasSlotIndexedComponent(Renderer renderer, out string offender)
        {
            offender = null;
            foreach (var component in renderer.GetComponents<Component>())
            {
                if (component == null) continue;
                if (component is Transform || component is Renderer || component is MeshFilter) continue;

                try
                {
                    var so = new SerializedObject(component);
                    var it = so.GetIterator();
                    while (it.NextVisible(true))
                    {
                        if (!it.isArray || it.propertyType == SerializedPropertyType.String) continue;
                        foreach (var name in SlotIndexedFieldNames)
                        {
                            if (!it.name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                            offender = component.GetType().Name;
                            return true;
                        }
                    }
                }
                catch (Exception)
                {
                    // EN: If we cannot inspect it, assume the worst.
                    // ZH: 无法内省时按最坏情况处理。
                    offender = component.GetType().Name;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// EN: Renumber <c>m_Materials.Array.data[i]</c> bindings for this object path. We only reach this
        ///     code when no slot is animated, so in practice this is a belt-and-braces cleanup of stale
        ///     bindings that point past the new array length.
        /// ZH: 为该对象路径重新编号 <c>m_Materials.Array.data[i]</c> 绑定。
        ///     只有在没有任何槽被动画驱动时才会走到这里，因此实际上这是对
        ///     “指向超出新数组长度的陈旧绑定”的双保险清理。
        /// </summary>
        private static void RewriteMaterialArrayBindings(BuildContext ctx, string path, int[] remap,
            int oldCount, int newCount)
        {
            AnimatorServicesContext asc;
            try { asc = ctx.Extension<AnimatorServicesContext>(); }
            catch (Exception) { return; }

            foreach (var controller in asc.ControllerContext.GetAllControllers())
            foreach (var node in controller.AllReachableNodes())
            {
                if (!(node is VirtualClip clip)) continue;

                foreach (var binding in clip.GetObjectCurveBindings().ToList())
                {
                    if (binding.path != path) continue;
                    if (!binding.propertyName.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal))
                        continue;

                    int open = binding.propertyName.IndexOf('[');
                    int close = binding.propertyName.IndexOf(']', open + 1);
                    if (open < 0 || close < 0) continue;
                    if (!int.TryParse(binding.propertyName.Substring(open + 1, close - open - 1), out var index))
                        continue;
                    if (index < 0 || index >= oldCount) continue;

                    int newIndex = remap[index];
                    if (newIndex == index) continue;

                    var curve = clip.GetObjectCurve(binding);
                    clip.SetObjectCurve(binding, null);

                    var newBinding = new EditorCurveBinding
                    {
                        path = binding.path,
                        type = binding.type,
                        propertyName = $"m_Materials.Array.data[{newIndex}]",
                    };
                    clip.SetObjectCurve(newBinding, curve);

                    ATOLog.Debug_($"remapped animation binding '{path}' slot {index} -> {newIndex}");
                }
            }
        }
    }
}
