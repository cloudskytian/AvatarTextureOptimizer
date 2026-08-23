using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>EN: Extracts connected UV islands and merges geometrically overlapping islands. ZH: 提取连通 UV 岛并合并几何重叠岛。</summary>
    internal static class UvIslandExtractor
    {
        private const float Epsilon = 1e-6f;

        private readonly struct PointKey : IEquatable<PointKey>
        {
            private readonly long _x, _y;
            public PointKey(Vector2 value) { _x = (long)Math.Round(value.x * 1000000d); _y = (long)Math.Round(value.y * 1000000d); }
            public bool Equals(PointKey other) => _x == other._x && _y == other._y;
            public override bool Equals(object obj) => obj is PointKey other && Equals(other);
            public override int GetHashCode() => (_x.GetHashCode() * 397) ^ _y.GetHashCode();
            public int CompareTo(PointKey other) { var x = _x.CompareTo(other._x); return x != 0 ? x : _y.CompareTo(other._y); }
        }

        private readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            private readonly PointKey _a, _b;
            public EdgeKey(Vector2 a, Vector2 b)
            {
                var aa = new PointKey(a); var bb = new PointKey(b);
                if (aa.CompareTo(bb) <= 0) { _a = aa; _b = bb; } else { _a = bb; _b = aa; }
            }
            public bool Equals(EdgeKey other) => _a.Equals(other._a) && _b.Equals(other._b);
            public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
            public override int GetHashCode() => (_a.GetHashCode() * 397) ^ _b.GetHashCode();
        }

        public static List<UvIsland> Extract(Mesh mesh, int subMesh, int uvChannel, int uvGroupId,
            out Vector2 integerTranslation, out string failure)
        {
            integerTranslation = Vector2.zero;
            failure = null;
            var uvs = new List<Vector2>(mesh.vertexCount);
            mesh.GetUVs(uvChannel, uvs);
            if (uvs.Count != mesh.vertexCount) { failure = $"UV{uvChannel} is missing or has the wrong vertex count"; return new List<UvIsland>(); }
            var indices = mesh.GetTriangles(subMesh, true);
            if (indices.Length == 0 || indices.Length % 3 != 0) return new List<UvIsland>();
            var triangles = new IslandTriangle[indices.Length / 3];
            for (var i = 0; i < triangles.Length; i++) triangles[i] = new IslandTriangle(indices[i * 3], indices[i * 3 + 1], indices[i * 3 + 2]);

            if (!TryNormalize(uvs, indices, out integerTranslation))
            {
                failure = "UVs cross a wrap seam or cannot be translated by an integer tile into [0,1]";
                return new List<UvIsland>();
            }
            if (integerTranslation != Vector2.zero)
                for (var i = 0; i < uvs.Count; i++) uvs[i] += integerTranslation;

            var union = new UnionFind(triangles.Length);
            var edges = new Dictionary<EdgeKey, int>();
            for (var i = 0; i < triangles.Length; i++)
            {
                var t = triangles[i];
                Connect(new EdgeKey(uvs[t.A], uvs[t.B]), i);
                Connect(new EdgeKey(uvs[t.B], uvs[t.C]), i);
                Connect(new EdgeKey(uvs[t.C], uvs[t.A]), i);
            }

            void Connect(EdgeKey edge, int triangle)
            {
                if (edges.TryGetValue(edge, out var previous)) union.Union(previous, triangle);
                else edges[edge] = triangle;
            }

            var initial = BuildIslands(union.Groups().Values, triangles, uvs, uvGroupId);
            MergeOverlaps(initial, triangles, uvs);
            return RebuildMerged(initial, uvGroupId);
        }

        private static bool TryNormalize(IReadOnlyList<Vector2> uvs, IReadOnlyList<int> indices, out Vector2 translation)
        {
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (var index in indices) { min = Vector2.Min(min, uvs[index]); max = Vector2.Max(max, uvs[index]); }
            var tx = -(int)Math.Floor(min.x + Epsilon);
            var ty = -(int)Math.Floor(min.y + Epsilon);
            translation = new Vector2(tx, ty);
            return min.x + tx >= -Epsilon && min.y + ty >= -Epsilon && max.x + tx <= 1f + Epsilon && max.y + ty <= 1f + Epsilon;
        }

        private static List<UvIsland> BuildIslands(IEnumerable<List<int>> groups, IReadOnlyList<IslandTriangle> triangles,
            IReadOnlyList<Vector2> uvs, int groupId)
        {
            var result = new List<UvIsland>();
            foreach (var group in groups)
            {
                var island = new UvIsland { UvGroupId = groupId };
                foreach (var triangle in group) island.Triangles.Add(triangles[triangle]);
                island.UvBounds = Bounds(island.Triangles, uvs);
                island.NormalizedBounds = island.UvBounds;
                result.Add(island);
            }
            return result;
        }

        private static void MergeOverlaps(IReadOnlyList<UvIsland> islands, IReadOnlyList<IslandTriangle> triangles,
            IReadOnlyList<Vector2> uvs)
        {
            if (islands.Count < 2) return;
            var union = new UnionFind(islands.Count);
            for (var a = 0; a < islands.Count; a++)
            for (var b = a + 1; b < islands.Count; b++)
            {
                if (!Overlaps(islands[a].UvBounds, islands[b].UvBounds)) continue;
                if (IslandsIntersect(islands[a], islands[b], uvs)) union.Union(a, b);
            }
            foreach (var group in union.Groups().Values)
            {
                if (group.Count < 2) continue;
                var target = islands[group[0]];
                for (var i = 1; i < group.Count; i++)
                {
                    target.Triangles.AddRange(islands[group[i]].Triangles);
                    islands[group[i]].Triangles.Clear();
                }
                target.UvBounds = Bounds(target.Triangles, uvs);
            }
        }

        private static List<UvIsland> RebuildMerged(IEnumerable<UvIsland> islands, int groupId)
        {
            var result = islands.Where(x => x.Triangles.Count > 0).ToList();
            for (var i = 0; i < result.Count; i++) { result[i].Id = i; result[i].UvGroupId = groupId; }
            return result;
        }

        private static bool IslandsIntersect(UvIsland a, UvIsland b, IReadOnlyList<Vector2> uv)
        {
            foreach (var ta in a.Triangles)
            foreach (var tb in b.Triangles)
                if (TrianglesIntersect(uv[ta.A], uv[ta.B], uv[ta.C], uv[tb.A], uv[tb.B], uv[tb.C])) return true;
            return false;
        }

        private static bool TrianglesIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector2 e, Vector2 f)
        {
            if (PointInTriangle(a, d, e, f) || PointInTriangle(d, a, b, c)) return true;
            var p = new[] { a, b, c }; var q = new[] { d, e, f };
            for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++)
                if (SegmentsIntersect(p[i], p[(i + 1) % 3], q[j], q[(j + 1) % 3])) return true;
            return false;
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            var d1 = Cross(p - b, a - b); var d2 = Cross(p - c, b - c); var d3 = Cross(p - a, c - a);
            var neg = d1 < -Epsilon || d2 < -Epsilon || d3 < -Epsilon;
            var pos = d1 > Epsilon || d2 > Epsilon || d3 > Epsilon;
            return !(neg && pos);
        }

        private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            var abC = Cross(b - a, c - a); var abD = Cross(b - a, d - a);
            var cdA = Cross(d - c, a - c); var cdB = Cross(d - c, b - c);
            return abC * abD <= Epsilon && cdA * cdB <= Epsilon &&
                   Mathf.Max(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x)) <= Mathf.Min(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x)) + Epsilon &&
                   Mathf.Max(Mathf.Min(a.y, b.y), Mathf.Min(c.y, d.y)) <= Mathf.Min(Mathf.Max(a.y, b.y), Mathf.Max(c.y, d.y)) + Epsilon;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        private static bool Overlaps(Rect a, Rect b) => a.xMin <= b.xMax + Epsilon && a.xMax + Epsilon >= b.xMin && a.yMin <= b.yMax + Epsilon && a.yMax + Epsilon >= b.yMin;
        private static Rect Bounds(IEnumerable<IslandTriangle> triangles, IReadOnlyList<Vector2> uv)
        {
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity); var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (var t in triangles) { min = Vector2.Min(min, uv[t.A]); min = Vector2.Min(min, uv[t.B]); min = Vector2.Min(min, uv[t.C]); max = Vector2.Max(max, uv[t.A]); max = Vector2.Max(max, uv[t.B]); max = Vector2.Max(max, uv[t.C]); }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }
    }
}
