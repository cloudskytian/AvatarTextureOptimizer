// SPDX-License-Identifier: MIT
// EN: Mesh side geometry helpers: world space areas under blend shapes and animated scale, and UV
//     range validation / normalization.
// ZH: 网格侧几何辅助：考虑形态键与动画缩放的世界空间面积，以及 UV 范围校验与归一化。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Meshes
{
    /// <summary>
    /// EN: Result of validating a UV set against the [0,1] range.
    /// ZH: 针对 [0,1] 范围校验 UV 集的结果。
    /// </summary>
    public enum UvRangeStatus
    {
        /// <summary>EN: Already inside [0,1]. ZH: 已在 [0,1] 内。</summary>
        InRange,
        /// <summary>EN: Outside, but a whole-tile integer shift brings it back without crossing a seam. ZH: 越界，但整块整数平移即可归位且不跨缝。</summary>
        ShiftableToRange,
        /// <summary>EN: Outside and crossing a wrap seam; the texture must not be atlased. ZH: 越界且跨 wrap 缝；该贴图不可图集化。</summary>
        CrossesSeam,
    }

    /// <summary>
    /// EN: Geometry helpers.
    /// ZH: 几何辅助方法。
    /// </summary>
    public static class MeshGeometry
    {
        private const string Stage = "Geometry";

        /// <summary>
        /// EN: Returns the per triangle world space area, taking the maximum over the base pose and
        ///     every blend shape evaluated on its own at weight 100.
        ///     Combinations are deliberately not explored: with N shapes there are 2^N combinations, which
        ///     explodes immediately. Comparing each shape independently and keeping the per triangle
        ///     maximum is a safe upper bound for texel density purposes and stays O(shapes * triangles).
        /// ZH: 返回逐三角形的世界空间面积，取基础姿态与每个形态键单独置于权重 100 时的最大值。
        ///     刻意不枚举组合：N 个形态键有 2^N 种组合，会立刻爆炸。
        ///     独立比较每个形态键并逐三角形取最大值，对像素密度而言是安全上界，
        ///     且复杂度保持在 O(形态键数 × 三角形数)。
        /// </summary>
        /// <param name="mesh">EN: Source mesh. ZH: 源网格。</param>
        /// <param name="indices">EN: Triangle index list of one sub mesh. ZH: 某个子网格的三角形索引表。</param>
        /// <param name="lossyScale">EN: Largest world scale the renderer can reach. ZH: 渲染器可达到的最大世界缩放。</param>
        public static float[] TriangleMaxWorldAreas(Mesh mesh, int[] indices, Vector3 lossyScale)
        {
            int triangleCount = indices.Length / 3;
            var areas = new float[triangleCount];
            var baseVerts = mesh.vertices;

            for (int t = 0; t < triangleCount; t++)
            {
                areas[t] = TriangleWorldArea(
                    baseVerts[indices[t * 3]], baseVerts[indices[t * 3 + 1]], baseVerts[indices[t * 3 + 2]], lossyScale);
            }

            if (mesh.blendShapeCount == 0) return areas;

            // EN: Only vertices touched by this sub mesh matter, so build the touched set once.
            // ZH: 只有被该子网格引用的顶点才有意义，因此先构建一次被引用顶点集合。
            var touched = new bool[mesh.vertexCount];
            foreach (var i in indices) touched[i] = true;

            var deltaV = new Vector3[mesh.vertexCount];
            var deltaN = new Vector3[mesh.vertexCount];
            var deltaT = new Vector3[mesh.vertexCount];
            var shaped = new Vector3[mesh.vertexCount];

            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                int frames = mesh.GetBlendShapeFrameCount(s);
                if (frames <= 0) continue;

                // EN: Weight 100 is the frame whose authored weight is closest to 100.
                // ZH: 权重 100 对应编辑时权重最接近 100 的那一帧。
                int bestFrame = 0;
                float bestDelta = float.MaxValue;
                for (int f = 0; f < frames; f++)
                {
                    float d = Mathf.Abs(mesh.GetBlendShapeFrameWeight(s, f) - 100f);
                    if (d < bestDelta) { bestDelta = d; bestFrame = f; }
                }

                mesh.GetBlendShapeFrameVertices(s, bestFrame, deltaV, deltaN, deltaT);

                bool affectsSubMesh = false;
                for (int v = 0; v < mesh.vertexCount; v++)
                {
                    shaped[v] = baseVerts[v] + deltaV[v];
                    if (!affectsSubMesh && touched[v] && deltaV[v].sqrMagnitude > 1e-12f) affectsSubMesh = true;
                }
                if (!affectsSubMesh) continue;

                for (int t = 0; t < triangleCount; t++)
                {
                    float a = TriangleWorldArea(
                        shaped[indices[t * 3]], shaped[indices[t * 3 + 1]], shaped[indices[t * 3 + 2]], lossyScale);
                    if (a > areas[t]) areas[t] = a;
                }
            }

            return areas;
        }

        /// <summary>
        /// EN: World space area of a triangle at the largest scale the renderer can reach.
        /// ZH: 渲染器可达最大缩放下三角形的世界空间面积。
        /// </summary>
        public static float TriangleWorldArea(Vector3 a, Vector3 b, Vector3 c, Vector3 lossyScale)
        {
            var sa = Vector3.Scale(a, lossyScale);
            var sb = Vector3.Scale(b, lossyScale);
            var sc = Vector3.Scale(c, lossyScale);
            return Vector3.Cross(sb - sa, sc - sa).magnitude * 0.5f;
        }

        /// <summary>
        /// EN: Classifies a set of UVs and, when possible, returns the integer tile shift that brings them
        ///     back into [0,1].
        /// ZH: 对一组 UV 分类；可能时返回把它们平移回 [0,1] 的整数块偏移。
        /// </summary>
        /// <param name="uvs">EN: UV coordinates to inspect. ZH: 待检查的 UV 坐标。</param>
        /// <param name="indices">EN: Indices into <paramref name="uvs"/> that belong to the region. ZH: 属于该区域的 <paramref name="uvs"/> 索引。</param>
        /// <param name="shift">EN: Integer shift to apply. ZH: 需要应用的整数偏移。</param>
        public static UvRangeStatus ClassifyRange(IReadOnlyList<Vector2> uvs, IReadOnlyList<int> indices, out Vector2Int shift)
        {
            shift = Vector2Int.zero;
            if (indices.Count == 0) return UvRangeStatus.InRange;

            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var i in indices)
            {
                var uv = uvs[i];
                minX = Mathf.Min(minX, uv.x); maxX = Mathf.Max(maxX, uv.x);
                minY = Mathf.Min(minY, uv.y); maxY = Mathf.Max(maxY, uv.y);
            }

            const float eps = 1e-4f;
            if (minX >= -eps && maxX <= 1f + eps && minY >= -eps && maxY <= 1f + eps)
                return UvRangeStatus.InRange;

            // EN: A whole-tile shift only works when the region spans strictly less than one tile.
            // ZH: 只有当区域跨度严格小于一个块时，整块平移才成立。
            if (maxX - minX > 1f + eps || maxY - minY > 1f + eps)
                return UvRangeStatus.CrossesSeam;

            int sx = Mathf.FloorToInt(minX + eps);
            int sy = Mathf.FloorToInt(minY + eps);
            if (maxX - sx > 1f + eps || maxY - sy > 1f + eps)
                return UvRangeStatus.CrossesSeam;

            shift = new Vector2Int(-sx, -sy);
            return UvRangeStatus.ShiftableToRange;
        }

        /// <summary>
        /// EN: Reads a UV channel, returning null when the mesh does not have it.
        /// ZH: 读取一个 UV 通道，网格没有该通道时返回 null。
        /// </summary>
        public static List<Vector2> GetUv(Mesh mesh, int channel)
        {
            var list = new List<Vector2>();
            mesh.GetUVs(channel, list);
            if (list.Count == 0) return null;
            if (list.Count != mesh.vertexCount)
            {
                AtoLog.Warning(Stage, $"mesh '{mesh.name}' UV{channel} has {list.Count} entries for {mesh.vertexCount} vertices; ignoring.");
                return null;
            }
            return list;
        }
    }
}
