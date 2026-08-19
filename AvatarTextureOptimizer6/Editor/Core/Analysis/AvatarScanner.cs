using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// Avatar 扫描器：收集所有（可能）可见的 Renderer、材质槽、网格数据。
    /// 跳过 EditorOnly；仅保留"当前启用或动画可能启用"的 Renderer。
    /// </summary>
    public sealed class AvatarScanner
    {
        private readonly GameObject _root;
        private readonly AnimationAnalysis _animation;

        public readonly List<Renderer> Renderers = new List<Renderer>();
        public readonly List<SlotSnapshot> Slots = new List<SlotSnapshot>();
        public readonly Dictionary<Renderer, Mesh> RendererMesh = new Dictionary<Renderer, Mesh>();
        public readonly Dictionary<Renderer, bool> RendererMaybeEnabled = new Dictionary<Renderer, bool>();

        public AvatarScanner(GameObject root, AnimationAnalysis animation)
        {
            _root = root;
            _animation = animation;
        }

        public void Scan()
        {
            foreach (var r in _root.GetComponentsInChildren<Renderer>(true))
            {
                if (IsEditorOnly(r)) continue;

                var skinned = r as SkinnedMeshRenderer;
                Mesh mesh = null;
                if (skinned != null)
                {
                    mesh = skinned.sharedMesh;
                }
                else if (r is MeshRenderer mr)
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }
                if (mesh == null) continue;

                bool maybeEnabled = r.enabled || _animation.RenderersMaybeEnabled.Contains(r)
                                    || MaybeEnabledByAncestors(r);
                if (!maybeEnabled) continue; // 从未启用且无动画启用 → 不可见

                Renderers.Add(r);
                RendererMesh[r] = mesh;
                RendererMaybeEnabled[r] = maybeEnabled;

                var mats = r.sharedMaterials;
                int subMeshCount = mesh.subMeshCount;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null) continue;
                    if (i >= subMeshCount)
                    {
                        // 材质数多于子网格：多余材质未渲染，跳过（但仍记录在动画切换集合中）
                        continue;
                    }
                    var tri = mesh.GetTriangles(i);
                    Slots.Add(new SlotSnapshot
                    {
                        renderer = r,
                        slotIndex = i,
                        material = mat,
                        triangleStart = 0,
                        triangleCount = tri.Length,
                    });
                }
            }
        }

        private bool MaybeEnabledByAncestors(Renderer r)
        {
            // 动画分析器已经把"带 m_IsActive 曲线对象下的所有 Renderer"加入 RenderersMaybeEnabled，
            // 因此这里只需兜底：自身 GameObject 当前激活即视为可见。
            return r.gameObject.activeInHierarchy;
        }

        private static bool IsEditorOnly(Renderer r)
        {
            var t = r.transform;
            while (t != null)
            {
                if (t.CompareTag("EditorOnly")) return true;
                t = t.parent;
            }
            return false;
        }
    }
}
