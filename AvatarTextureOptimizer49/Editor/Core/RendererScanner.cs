using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Collects renderers/material slots to process: only SkinnedMeshRenderer/MeshRenderer that are
    /// enabled now or animated on, excluding EditorOnly (already removed by NDMF, kept defensive).
    /// / 收集要处理的渲染器与材质槽：仅 SMR/MR、当前启用或被动画启用；EditorOnly 已被 NDMF 移除（保留防御）。
    /// </summary>
    internal static class RendererScanner
    {
        internal static List<RendererInfo> Scan(GameObject root)
        {
            var result = new List<RendererInfo>();

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;

                var go = r.gameObject;
                // EditorOnly objects are stripped by VRChat / removed by NDMF before us. / 防御性跳过。
                try
                {
                    if (go.CompareTag("EditorOnly")) continue;
                }
                catch
                {
                    // missing tag — ignore / 标签缺失则忽略
                }

                var mesh = GetMesh(r);
                if (mesh == null) continue;

                var info = new RendererInfo
                {
                    renderer = r,
                    mesh = mesh,
                    smr = r as SkinnedMeshRenderer,
                    include = go.activeInHierarchy && r.enabled,
                    slots = r.sharedMaterials,
                };
                result.Add(info);
            }

            return result;
        }

        internal static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer s) return s.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf == null ? null : mf.sharedMesh;
        }

        /// <summary>
        /// Effective world-area factor: renderer's own transform chain, using each ancestor's max
        /// animated |scale| (animation facts) or current scale. Conservative (max axis).
        /// / 有效面积因子：沿父链取各节点动画最大缩放或当前缩放（按最大轴，保守）。
        /// </summary>
        internal static float ComputeAreaFactor(RendererInfo info,
            Dictionary<Transform, Vector3> maxAnimScale)
        {
            var t = info.renderer.transform;
            float factor = 1f;
            while (t != null)
            {
                Vector3 s;
                if (maxAnimScale != null && maxAnimScale.TryGetValue(t, out var anim)) s = anim;
                else s = t.localScale;
                float m = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
                if (m > 0f && !float.IsInfinity(m) && !float.IsNaN(m)) factor *= m * m;
                t = t.parent;
            }

            return Mathf.Max(factor, 1e-6f);
        }
    }
}
