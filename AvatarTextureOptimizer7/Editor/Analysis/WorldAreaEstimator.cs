using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// World-space island area. Blendshapes: max of weight 0 and 100 only.
    /// Animation scale: max |scale| on the renderer (lossy, squared for area).
    /// 世界空间岛面积。形态键只取 0 与 100 的最大。动画缩放取渲染器最大 |scale|（面积按平方）。
    /// </summary>
    public static class WorldAreaEstimator
    {
        public static float Estimate(Renderer renderer, Mesh mesh, UvIsland island, AnimationCollector.RendererAnim anim)
        {
            if (mesh == null || island == null || island.Triangles.Count < 3) return 0f;
            var verts = mesh.vertices;
            if (verts == null || verts.Length == 0) return 0f;

            var rest = AreaOf(verts, island.Triangles);
            var maxLocal = rest;

            if (renderer is SkinnedMeshRenderer && mesh.blendShapeCount > 0)
            {
                var delta = new Vector3[verts.Length];
                var work = new Vector3[verts.Length];
                for (int s = 0; s < mesh.blendShapeCount; s++)
                {
                    var frames = mesh.GetBlendShapeFrameCount(s);
                    if (frames <= 0) continue;
                    // Frame closest to 100, and 0 is rest. / 取最接近 100 的帧，0 即 rest。
                    int frame = frames - 1;
                    float w = mesh.GetBlendShapeFrameWeight(s, frame);
                    if (Mathf.Abs(w) < 1e-4f) continue;
                    mesh.GetBlendShapeFrameVertices(s, frame, delta, null, null);
                    for (int i = 0; i < verts.Length; i++) work[i] = verts[i] + delta[i];
                    maxLocal = Mathf.Max(maxLocal, AreaOf(work, island.Triangles));
                    // Weight 0 is rest, already counted. Negative / >100 ignored by spec.
                    // 权重 0 即 rest。按需求忽略负数与超过 100。
                }
            }

            var lossy = renderer != null ? renderer.transform.lossyScale : Vector3.one;
            var scale2 = Mathf.Max(lossy.x * lossy.x, Mathf.Max(lossy.y * lossy.y, lossy.z * lossy.z));
            if (anim.MaxScaleSqr > scale2) scale2 = anim.MaxScaleSqr;
            if (scale2 < 1e-8f) scale2 = 1f;
            // Parent animation is approximated by current lossy * clip local max.
            // 父级动画用当前 lossy × clip 局部最大近似。
            return maxLocal * scale2;
        }

        static float AreaOf(Vector3[] verts, List<int> tris)
        {
            double a = 0;
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                var i0 = tris[i];
                var i1 = tris[i + 1];
                var i2 = tris[i + 2];
                if ((uint)i0 >= (uint)verts.Length || (uint)i1 >= (uint)verts.Length || (uint)i2 >= (uint)verts.Length)
                    continue;
                var e1 = verts[i1] - verts[i0];
                var e2 = verts[i2] - verts[i0];
                a += 0.5 * Vector3.Cross(e1, e2).magnitude;
            }

            return (float)a;
        }
    }
}
