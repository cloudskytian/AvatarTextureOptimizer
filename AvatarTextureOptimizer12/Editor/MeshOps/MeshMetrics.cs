// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - World-space mesh metrics (blendshape / animated-scale aware).
// AvatarTextureOptimizer (ATO) - 世界空间网格度量（考虑形态键与动画缩放）。

using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.MeshOps
{
    /// <summary>
    /// EN: Computes the world-space surface area of every triangle so that the quality pass can convert a
    ///     target texel density (px/m) into a pixel budget for each UV island.
    ///     Blendshapes are evaluated only at 0 and 100 (per-shape maximum, no combinatorial explosion) and
    ///     animated object scale is taken at its maximum, exactly as specified.
    /// ZH: 计算每个三角形的世界空间表面积，使质量阶段能把目标像素密度（px/m）换算成每个 UV 岛的像素预算。
    ///     形态键仅在 0 与 100 两点评估（逐形态取最大值，避免组合爆炸），
    ///     动画缩放按最大缩放计算，与需求完全一致。
    /// </summary>
    public static class MeshMetrics
    {
        /// <summary>
        /// EN: Per-triangle world area for a renderer, already including the maximum blendshape expansion
        ///     and the maximum animated scale.
        /// ZH: 某个渲染器的逐三角形世界面积，已包含形态键的最大膨胀与动画的最大缩放。
        /// </summary>
        public static float[] ComputeTriangleWorldAreas(Renderer renderer, Mesh mesh, int[] triangles,
            float maxAnimatedScale)
        {
            var result = new float[triangles.Length / 3];
            if (mesh == null || triangles.Length == 0) return result;

            var basePositions = mesh.vertices;
            var positions = new Vector3[basePositions.Length];
            System.Array.Copy(basePositions, positions, basePositions.Length);

            // ---- Blendshapes: take the per-vertex maximum displacement over {0, 100} ----
            // ---- 形态键：在 {0, 100} 两点上逐顶点取最大位移 ----
            var smr = renderer as SkinnedMeshRenderer;
            if (smr != null && mesh.blendShapeCount > 0)
            {
                ApplyMaxBlendshapeEnvelope(mesh, positions);
            }

            var lossy = renderer != null ? renderer.transform.lossyScale : Vector3.one;
            var scale = new Vector3(
                Mathf.Abs(lossy.x) * maxAnimatedScale,
                Mathf.Abs(lossy.y) * maxAnimatedScale,
                Mathf.Abs(lossy.z) * maxAnimatedScale);

            for (int t = 0; t < result.Length; t++)
            {
                var a = Vector3.Scale(positions[triangles[t * 3 + 0]], scale);
                var b = Vector3.Scale(positions[triangles[t * 3 + 1]], scale);
                var c = Vector3.Scale(positions[triangles[t * 3 + 2]], scale);
                result[t] = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }

            return result;
        }

        /// <summary>
        /// EN: Envelope of the rest pose and every single blendshape at 100. We take, per vertex, whichever
        ///     variant is furthest from the mesh centroid, which upper-bounds the surface area without
        ///     evaluating 2^n combinations.
        /// ZH: 静止姿态与每个形态键单独取 100 时的包络。对每个顶点取离网格重心最远的变体，
        ///     这在不评估 2^n 种组合的前提下给出表面积的上界。
        /// </summary>
        private static void ApplyMaxBlendshapeEnvelope(Mesh mesh, Vector3[] positions)
        {
            int vertexCount = positions.Length;
            var centroid = Vector3.zero;
            for (int i = 0; i < vertexCount; i++) centroid += positions[i];
            centroid /= Mathf.Max(1, vertexCount);

            var deltaV = new Vector3[vertexCount];
            var deltaN = new Vector3[vertexCount];
            var deltaT = new Vector3[vertexCount];

            var bestDist = new float[vertexCount];
            for (int i = 0; i < vertexCount; i++) bestDist[i] = (positions[i] - centroid).sqrMagnitude;

            var best = new Vector3[vertexCount];
            System.Array.Copy(positions, best, vertexCount);

            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                int frames = mesh.GetBlendShapeFrameCount(s);
                if (frames <= 0) continue;

                // EN: Frame that corresponds to weight 100 (the last frame at or below 100).
                // ZH: 对应权重 100 的帧（最后一个不超过 100 的帧）。
                int frame = frames - 1;
                for (int f = 0; f < frames; f++)
                {
                    if (mesh.GetBlendShapeFrameWeight(s, f) <= 100f) frame = f;
                }

                mesh.GetBlendShapeFrameVertices(s, frame, deltaV, deltaN, deltaT);
                float weight = mesh.GetBlendShapeFrameWeight(s, frame);
                float k = weight > 0f ? 100f / weight : 1f;

                for (int i = 0; i < vertexCount; i++)
                {
                    var p = positions[i] + deltaV[i] * k;
                    float d = (p - centroid).sqrMagnitude;
                    if (d > bestDist[i])
                    {
                        bestDist[i] = d;
                        best[i] = p;
                    }
                }
            }

            System.Array.Copy(best, positions, vertexCount);
        }

        /// <summary>
        /// EN: Largest absolute scale factor an animation ever applies to this transform chain.
        ///     Returns 1 when nothing animates the scale.
        /// ZH: 动画对该 Transform 链施加过的最大绝对缩放系数。没有任何动画修改缩放时返回 1。
        /// </summary>
        public static float MaxAnimatedScale(Transform t, IReadOnlyDictionary<string, float> animatedScaleByPath,
            Transform avatarRoot)
        {
            float result = 1f;
            var cur = t;
            while (cur != null)
            {
                var path = nadena.dev.ndmf.runtime.RuntimeUtil.RelativePath(avatarRoot.gameObject, cur.gameObject);
                if (path != null && animatedScaleByPath.TryGetValue(path, out var s) && s > result) result = s;
                if (cur == avatarRoot) break;
                cur = cur.parent;
            }
            if (result != 1f) ATOLog.Trace($"max animated scale for '{t.name}' = {result}");
            return result;
        }
    }
}
