// Copyright (c) fosa. Licensed under the MIT License.
// Computes world-space surface area per UV island, accounting for blend shapes and animated
// scale, so texel density can be clamped against real-world size.
// 计算每个 UV 岛的世界空间表面积，并考虑形态键与动画缩放，使像素密度能够按真实尺寸钳制。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Measures how much physical surface each UV island covers. Islands covering a large
    /// surface need more texels than tiny ones, which is what the pixel-density clamp encodes.
    /// 测量每个 UV 岛覆盖的物理表面积。覆盖大面积的岛比微小的岛需要更多 texel，
    /// 这正是像素密度钳制所表达的含义。
    /// </summary>
    public static class MeshAreaAnalyzer
    {
        /// <summary>
        /// Computes per-island world-space area and UV area, returning the resulting texel
        /// density requirement in pixels per metre.
        /// 计算每个岛的世界空间面积与 UV 面积，返回以 px/m 表示的像素密度需求。
        /// </summary>
        /// <param name="mesh">Source mesh. / 源网格。</param>
        /// <param name="triangles">Triangle indices of the relevant submesh set. / 相关子网格集合的三角形索引。</param>
        /// <param name="uvs">UV coordinates. / UV 坐标。</param>
        /// <param name="islands">Islands to measure. / 待测量的岛。</param>
        /// <param name="worldScale">Maximum world scale to apply. / 需要施加的最大世界缩放。</param>
        public static void ComputeIslandAreas(
            Mesh mesh, int[] triangles, Vector2[] uvs, List<UVIsland> islands, Vector3 worldScale)
        {
            if (mesh == null || islands == null) return;

            var vertices = GetMaxExtentVertices(mesh);
            if (vertices == null || vertices.Length == 0) return;

            foreach (var island in islands)
            {
                double worldArea = 0;

                foreach (var t in island.Triangles)
                {
                    var i0 = triangles[t * 3];
                    var i1 = triangles[t * 3 + 1];
                    var i2 = triangles[t * 3 + 2];

                    if (i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length)
                        continue;

                    var p0 = Vector3.Scale(vertices[i0], worldScale);
                    var p1 = Vector3.Scale(vertices[i1], worldScale);
                    var p2 = Vector3.Scale(vertices[i2], worldScale);

                    worldArea += TriangleArea(p0, p1, p2);
                }

                island.WorldArea = (float)worldArea;
            }
        }

        /// <summary>
        /// Returns vertex positions expanded to their maximum extent across blend shapes.
        /// Per the specification each blend shape is evaluated only at 0 and 100 and the larger
        /// displacement wins, avoiding a combinatorial explosion over shape permutations.
        /// 返回按形态键最大范围扩展后的顶点位置。
        /// 依据需求，每个形态键只在 0 与 100 处评估并取位移较大者，从而避免形态组合爆炸。
        /// </summary>
        public static Vector3[] GetMaxExtentVertices(Mesh mesh)
        {
            var baseVerts = mesh.vertices;
            if (baseVerts == null || baseVerts.Length == 0) return baseVerts;

            var shapeCount = mesh.blendShapeCount;
            if (shapeCount == 0) return baseVerts;

            var result = new Vector3[baseVerts.Length];
            Array.Copy(baseVerts, result, baseVerts.Length);

            var deltaV = new Vector3[baseVerts.Length];
            var deltaN = new Vector3[baseVerts.Length];
            var deltaT = new Vector3[baseVerts.Length];

            // Track the largest absolute displacement seen on each axis for each vertex.
            // 记录每个顶点在各轴上出现过的最大绝对位移。
            var maxDisp = new Vector3[baseVerts.Length];

            for (var s = 0; s < shapeCount; s++)
            {
                var frames = mesh.GetBlendShapeFrameCount(s);
                if (frames <= 0) continue;

                // Only the final frame matters: it corresponds to a weight of 100.
                // 只有最后一帧重要，它对应权重 100。
                mesh.GetBlendShapeFrameVertices(s, frames - 1, deltaV, deltaN, deltaT);

                for (var v = 0; v < baseVerts.Length; v++)
                {
                    var d = deltaV[v];
                    if (Mathf.Abs(d.x) > Mathf.Abs(maxDisp[v].x)) maxDisp[v].x = d.x;
                    if (Mathf.Abs(d.y) > Mathf.Abs(maxDisp[v].y)) maxDisp[v].y = d.y;
                    if (Mathf.Abs(d.z) > Mathf.Abs(maxDisp[v].z)) maxDisp[v].z = d.z;
                }
            }

            for (var v = 0; v < baseVerts.Length; v++)
            {
                result[v] = baseVerts[v] + maxDisp[v];
            }

            return result;
        }

        /// <summary>Area of a triangle in world units. / 三角形在世界单位下的面积。</summary>
        public static float TriangleArea(Vector3 a, Vector3 b, Vector3 c)
        {
            return Vector3.Cross(b - a, c - a).magnitude * 0.5f;
        }

        /// <summary>Area of a triangle in UV space. / 三角形在 UV 空间中的面积。</summary>
        public static float UVTriangleArea(Vector2 a, Vector2 b, Vector2 c)
        {
            return Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) * 0.5f;
        }

        /// <summary>
        /// Computes the UV-space area covered by an island.
        /// 计算一个岛在 UV 空间中覆盖的面积。
        /// </summary>
        public static float ComputeUVArea(UVIsland island, int[] triangles, Vector2[] uvs)
        {
            double area = 0;
            foreach (var t in island.Triangles)
            {
                var i0 = triangles[t * 3];
                var i1 = triangles[t * 3 + 1];
                var i2 = triangles[t * 3 + 2];
                if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
                area += UVTriangleArea(uvs[i0], uvs[i1], uvs[i2]);
            }

            return (float)area;
        }

        /// <summary>
        /// Derives the pixel dimensions an island needs to satisfy a texel-density target.
        /// The result is clamped by the island's true size in the source texture, since we can
        /// never invent detail that was not present in the original asset.
        /// 依据像素密度目标推导岛所需的像素尺寸。
        /// 结果会被岛在源贴图中的真实尺寸钳制，因为我们无法凭空创造原始资产中不存在的细节。
        /// </summary>
        public static Vector2Int ComputeDensityTarget(
            UVIsland island,
            int[] triangles,
            Vector2[] uvs,
            int sourceWidth,
            int sourceHeight,
            int minDensity,
            int maxDensity)
        {
            var uvArea = ComputeUVArea(island, triangles, uvs);
            var srcW = Mathf.Max(1, Mathf.CeilToInt(island.UVBounds.width * sourceWidth));
            var srcH = Mathf.Max(1, Mathf.CeilToInt(island.UVBounds.height * sourceHeight));

            if (uvArea <= 1e-9f || island.WorldArea <= 1e-9f)
            {
                return new Vector2Int(srcW, srcH);
            }

            // Metres of surface spanned by one unit of UV, along each axis. The square root
            // converts the area ratio into a linear ratio.
            // 每单位 UV 沿各轴跨越的表面米数。开平方将面积比转换为线性比。
            var metresPerUV = Mathf.Sqrt(island.WorldArea / uvArea);

            var minW = Mathf.CeilToInt(island.UVBounds.width * metresPerUV * minDensity);
            var minH = Mathf.CeilToInt(island.UVBounds.height * metresPerUV * minDensity);
            var maxW = Mathf.CeilToInt(island.UVBounds.width * metresPerUV * maxDensity);
            var maxH = Mathf.CeilToInt(island.UVBounds.height * metresPerUV * maxDensity);

            // Clamp to what the source texture can actually provide.
            // 钳制到源贴图实际能够提供的范围。
            var w = Mathf.Clamp(srcW, Mathf.Min(minW, srcW), Mathf.Max(1, Mathf.Min(maxW, srcW)));
            var h = Mathf.Clamp(srcH, Mathf.Min(minH, srcH), Mathf.Max(1, Mathf.Min(maxH, srcH)));

            return new Vector2Int(Mathf.Max(1, w), Mathf.Max(1, h));
        }
    }
}
