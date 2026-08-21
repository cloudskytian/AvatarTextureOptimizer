using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Mesh and UV analysis helpers for island extraction.
    /// 用于 UV 岛提取的网格与 UV 分析辅助工具。
    /// </summary>
    internal static class AtoMeshAlgorithms
    {
        public static List<AtoUvIslandRecord> ExtractIslands(Mesh mesh, int subMeshIndex, int uvChannel, out float totalObjectArea, out float totalUvArea)
        {
            totalObjectArea = 0.0f;
            totalUvArea = 0.0f;
            var islands = new List<AtoUvIslandRecord>();
            if (mesh == null || subMeshIndex < 0 || subMeshIndex >= mesh.subMeshCount)
            {
                return islands;
            }

            var uvs = new List<Vector2>();
            mesh.GetUVs(uvChannel, uvs);
            if (uvs.Count == 0)
            {
                return islands;
            }

            var vertices = mesh.vertices;
            var indices = mesh.GetTriangles(subMeshIndex);
            if (indices == null || indices.Length < 3)
            {
                return islands;
            }

            var blendShapeVertices = LoadBlendShapeVertices(mesh, vertices.Length);
            var triangles = new List<WorkingTriangle>();
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                var ia = indices[i];
                var ib = indices[i + 1];
                var ic = indices[i + 2];
                if (ia < 0 || ib < 0 || ic < 0 || ia >= vertices.Length || ib >= vertices.Length || ic >= vertices.Length)
                {
                    continue;
                }

                if (ia >= uvs.Count || ib >= uvs.Count || ic >= uvs.Count)
                {
                    continue;
                }

                var objectArea = ComputeMaxObjectTriangleArea(vertices, blendShapeVertices, ia, ib, ic);
                var uvArea = ComputeUvTriangleArea(uvs[ia], uvs[ib], uvs[ic]);
                var triangle = new WorkingTriangle(uvs[ia], uvs[ib], uvs[ic], objectArea, uvArea);
                triangles.Add(triangle);
                totalObjectArea += triangle.ObjectArea;
                totalUvArea += triangle.UvArea;
            }

            if (triangles.Count == 0)
            {
                return islands;
            }

            var adjacency = BuildAdjacency(triangles);
            var visited = new bool[triangles.Count];
            for (var i = 0; i < triangles.Count; i++)
            {
                if (visited[i])
                {
                    continue;
                }

                var island = new AtoUvIslandRecord { Index = islands.Count };
                var queue = new Queue<int>();
                queue.Enqueue(i);
                visited[i] = true;

                var first = triangles[i];
                island.Min = first.Min;
                island.Max = first.Max;

                while (queue.Count > 0)
                {
                    var currentIndex = queue.Dequeue();
                    var triangle = triangles[currentIndex];
                    island.TriangleCount++;
                    island.ObjectSpaceArea += triangle.ObjectArea;
                    island.UvArea += triangle.UvArea;
                    island.Min = Vector2.Min(island.Min, triangle.Min);
                    island.Max = Vector2.Max(island.Max, triangle.Max);
                    island.Triangles.Add(new AtoUvTriangleRecord(triangle.A, triangle.B, triangle.C));

                    foreach (var neighbor in adjacency[currentIndex])
                    {
                        if (visited[neighbor])
                        {
                            continue;
                        }

                        visited[neighbor] = true;
                        queue.Enqueue(neighbor);
                    }
                }

                island.Size = island.Max - island.Min;
                islands.Add(island);
            }

            return islands;
        }

        private static List<Vector3[]> LoadBlendShapeVertices(Mesh mesh, int vertexCount)
        {
            var result = new List<Vector3[]>();
            var frameVertices = new Vector3[vertexCount];
            var frameNormals = new Vector3[vertexCount];
            var frameTangents = new Vector3[vertexCount];
            for (var shapeIndex = 0; shapeIndex < mesh.blendShapeCount; shapeIndex++)
            {
                var frameCount = mesh.GetBlendShapeFrameCount(shapeIndex);
                if (frameCount <= 0)
                {
                    continue;
                }

                Array.Clear(frameVertices, 0, frameVertices.Length);
                Array.Clear(frameNormals, 0, frameNormals.Length);
                Array.Clear(frameTangents, 0, frameTangents.Length);
                mesh.GetBlendShapeFrameVertices(shapeIndex, frameCount - 1, frameVertices, frameNormals, frameTangents);
                var clone = new Vector3[vertexCount];
                Array.Copy(frameVertices, clone, vertexCount);
                result.Add(clone);
            }

            return result;
        }

        private static float ComputeMaxObjectTriangleArea(Vector3[] vertices, IReadOnlyList<Vector3[]> blendShapeVertices, int ia, int ib, int ic)
        {
            var maxArea = ComputeObjectTriangleArea(vertices[ia], vertices[ib], vertices[ic]);
            foreach (var deltas in blendShapeVertices)
            {
                var area = ComputeObjectTriangleArea(vertices[ia] + deltas[ia], vertices[ib] + deltas[ib], vertices[ic] + deltas[ic]);
                if (area > maxArea)
                {
                    maxArea = area;
                }
            }

            return maxArea;
        }

        private static List<int>[] BuildAdjacency(IReadOnlyList<WorkingTriangle> triangles)
        {
            var adjacency = Enumerable.Range(0, triangles.Count).Select(_ => new List<int>()).ToArray();
            var edgeMap = new Dictionary<QuantizedUvEdge, List<int>>();

            for (var i = 0; i < triangles.Count; i++)
            {
                var triangle = triangles[i];
                RegisterEdge(edgeMap, new QuantizedUvEdge(triangle.A, triangle.B), i);
                RegisterEdge(edgeMap, new QuantizedUvEdge(triangle.B, triangle.C), i);
                RegisterEdge(edgeMap, new QuantizedUvEdge(triangle.C, triangle.A), i);
            }

            foreach (var pair in edgeMap)
            {
                var owners = pair.Value;
                if (owners.Count < 2)
                {
                    continue;
                }

                for (var i = 0; i < owners.Count; i++)
                {
                    for (var j = i + 1; j < owners.Count; j++)
                    {
                        var a = owners[i];
                        var b = owners[j];
                        if (!adjacency[a].Contains(b)) adjacency[a].Add(b);
                        if (!adjacency[b].Contains(a)) adjacency[b].Add(a);
                    }
                }
            }

            return adjacency;
        }

        private static void RegisterEdge(Dictionary<QuantizedUvEdge, List<int>> edgeMap, QuantizedUvEdge edge, int triangleIndex)
        {
            if (!edge.IsValid)
            {
                return;
            }

            if (!edgeMap.TryGetValue(edge, out var owners))
            {
                owners = new List<int>();
                edgeMap.Add(edge, owners);
            }

            owners.Add(triangleIndex);
        }

        private static float ComputeObjectTriangleArea(Vector3 a, Vector3 b, Vector3 c)
        {
            return Vector3.Cross(b - a, c - a).magnitude * 0.5f;
        }

        private static float ComputeUvTriangleArea(Vector2 a, Vector2 b, Vector2 c)
        {
            return Mathf.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) * 0.5f;
        }

        private readonly struct WorkingTriangle
        {
            public readonly Vector2 A;
            public readonly Vector2 B;
            public readonly Vector2 C;
            public readonly float ObjectArea;
            public readonly float UvArea;
            public Vector2 Min => Vector2.Min(A, Vector2.Min(B, C));
            public Vector2 Max => Vector2.Max(A, Vector2.Max(B, C));

            public WorkingTriangle(Vector2 a, Vector2 b, Vector2 c, float objectArea, float uvArea)
            {
                A = a;
                B = b;
                C = c;
                ObjectArea = objectArea;
                UvArea = uvArea;
            }
        }

        private readonly struct QuantizedUvEdge : IEquatable<QuantizedUvEdge>
        {
            private readonly int _ax;
            private readonly int _ay;
            private readonly int _bx;
            private readonly int _by;
            public bool IsValid { get; }

            public QuantizedUvEdge(Vector2 a, Vector2 b)
            {
                var qa = Quantize(a);
                var qb = Quantize(b);
                IsValid = qa != qb;
                var aBeforeB = qa.x < qb.x || (qa.x == qb.x && qa.y <= qb.y);
                if (aBeforeB)
                {
                    _ax = qa.x;
                    _ay = qa.y;
                    _bx = qb.x;
                    _by = qb.y;
                }
                else
                {
                    _ax = qb.x;
                    _ay = qb.y;
                    _bx = qa.x;
                    _by = qa.y;
                }
            }

            public bool Equals(QuantizedUvEdge other)
            {
                return _ax == other._ax && _ay == other._ay && _bx == other._bx && _by == other._by;
            }

            public override bool Equals(object obj)
            {
                return obj is QuantizedUvEdge other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hashCode = _ax;
                    hashCode = (hashCode * 397) ^ _ay;
                    hashCode = (hashCode * 397) ^ _bx;
                    hashCode = (hashCode * 397) ^ _by;
                    return hashCode;
                }
            }

            private static Vector2Int Quantize(Vector2 value)
            {
                return new Vector2Int(
                    Mathf.RoundToInt(value.x * 100000.0f),
                    Mathf.RoundToInt(value.y * 100000.0f));
            }
        }
    }
}
