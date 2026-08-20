using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.runtime;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>EN: One material slot we decided to look at. ZH: 我们决定处理的一个材质槽。</summary>
    public sealed class SlotRecord
    {
        /// <summary>EN: Renderer owning the slot. ZH: 拥有该槽的渲染器。</summary>
        public Renderer Renderer;
        /// <summary>EN: Slot / submesh index. ZH: 槽 / 子网格索引。</summary>
        public int Index;
        /// <summary>EN: Avatar-relative path of the renderer. ZH: 渲染器相对于 Avatar 的路径。</summary>
        public string Path;
        /// <summary>EN: The shared mesh. ZH: 共享网格。</summary>
        public Mesh Mesh;
        /// <summary>EN: Every material this slot can ever hold, including animated swaps. ZH: 该槽可能持有的所有材质，含动画切换。</summary>
        public readonly List<Material> Materials = new List<Material>();
    }

    /// <summary>
    /// EN: Walks the avatar and produces the list of material slots that participate in optimisation.
    ///     Skips EditorOnly subtrees and objects that are disabled and can never be enabled by animation.
    /// ZH: 遍历 Avatar，产出参与优化的材质槽列表。
    ///     跳过 EditorOnly 子树，以及被禁用且动画也无法启用的对象。
    /// </summary>
    public static class RendererCollector
    {
        /// <summary>EN: Collect slots. ZH: 收集材质槽。</summary>
        public static List<SlotRecord> Collect(BuildContext ctx, AnimationFacts anim, ATOLog log)
        {
            var root = ctx.AvatarRootTransform;
            var result = new List<SlotRecord>();

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                if (IsUnderEditorOnly(r.transform, root)) { log.Trace($"Skip EditorOnly renderer '{r.name}'"); continue; }

                var path = RuntimeUtil.RelativePath(root.gameObject, r.gameObject);
                if (!IsPotentiallyActive(r, root, anim, path))
                {
                    log.Trace($"Skip permanently-disabled renderer '{path}'");
                    continue;
                }

                Mesh mesh = r is SkinnedMeshRenderer smr
                    ? smr.sharedMesh
                    : (r.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null);
                if (mesh == null) continue;

                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var rec = new SlotRecord { Renderer = r, Index = i, Path = path, Mesh = mesh };
                    if (mats[i] != null) rec.Materials.Add(mats[i]);

                    // EN: Animation may swap this slot to other materials; all of them must be handled.
                    // ZH: 动画可能把该槽换成其他材质；所有这些材质都必须处理。
                    if (anim.AnimatedMaterials.TryGetValue(path + "#" + i, out var swaps))
                        foreach (var m in swaps) if (m != null && !rec.Materials.Contains(m)) rec.Materials.Add(m);

                    if (rec.Materials.Count > 0) result.Add(rec);
                }
            }

            log.Verbose($"Collected {result.Count} material slots from " +
                        $"{result.Select(s => s.Renderer).Distinct().Count()} renderers");
            return result;
        }

        private static bool IsUnderEditorOnly(Transform t, Transform root)
        {
            while (t != null)
            {
                if (t.CompareTag("EditorOnly")) return true;
                if (t == root) break;
                t = t.parent;
            }
            return false;
        }

        private static bool IsPotentiallyActive(Renderer r, Transform root, AnimationFacts anim, string path)
        {
            if (r.enabled && r.gameObject.activeInHierarchy) return true;
            if (anim.PathsAnimationCanEnable.Contains(path)) return true;

            // EN: A parent may be the disabled one, and animation may re-enable that parent instead.
            // ZH: 被禁用的可能是某个父级，而动画可能启用的正是那个父级。
            var t = r.transform;
            while (t != null && t != root.parent)
            {
                var p = RuntimeUtil.RelativePath(root.gameObject, t.gameObject);
                if (p != null && anim.PathsAnimationCanEnable.Contains(p)) return true;
                t = t.parent;
            }
            return false;
        }
    }
}
