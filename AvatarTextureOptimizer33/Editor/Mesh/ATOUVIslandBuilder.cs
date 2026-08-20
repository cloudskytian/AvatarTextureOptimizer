// SPDX-License-Identifier: MIT
// EN: UV island extraction, wrap normalisation, world area estimation (blend shapes + animated scale)
//     and overlapping island merging.
// ZH: UV 岛提取、wrap 归一化、世界面积估算（形态键 + 动画缩放）以及重叠岛合并。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: All islands of one <see cref="ATOUVKey"/> plus the flags discovered while building them.
    /// ZH: 某个 <see cref="ATOUVKey"/> 的全部 UV 岛，以及构建过程中发现的标记。
    /// </summary>
    public sealed class ATOIslandSet
    {
        public ATOUVKey Key;
        public readonly List<ATOIsland> Islands = new List<ATOIsland>();

        /// <summary>EN: UVs cross a wrap seam and cannot be repacked. ZH: UV 跨越 wrap 缝，无法重排。</summary>
        public bool CrossesWrapSeam;

        /// <summary>EN: Per vertex UV after wrap normalisation. ZH: wrap 归一化后的逐顶点 UV。</summary>
        public Vector2[] NormalisedUV;

        public override string ToString() => $"{Key}: {Islands.Count} islands";
    }

    /// <summary>
    /// EN: Builds island sets for the meshes of the avatar.
    /// ZH: 为 Avatar 的网格构建 UV 岛集合。
    /// </summary>
    public sealed class ATOUVIslandBuilder
    {
        private const float Epsilon = 1e-5f;

        private readonly ATOLog _log;
        private readonly bool _mergeOverlapping;

        private readonly Dictionary<Mesh, MeshGeometry> _geometry = new Dictionary<Mesh, MeshGeometry>();

        public ATOUVIslandBuilder(ATOLog log, bool mergeOverlapping)
        {
            _log = log;
            _mergeOverlapping = mergeOverlapping;
        }

        /// <summary>
        /// EN: Cached per mesh geometry: positions, worst case (blend shape aware) triangle areas.
        /// ZH: 按网格缓存的几何数据：顶点位置与考虑形态键后的最坏三角面积。
        /// </summary>
        private sealed class MeshGeometry
        {
            public Vector3[] Positions;
            public float[] TriangleArea; // EN: indexed by global triangle id. ZH: 以全局三角形 id 索引。
            public int[][] SubMeshTriangles;
            public int[] SubMeshTriangleOffset;
        }

        private MeshGeometry GetGeometry(Mesh mesh)
        {
            if (_geometry.TryGetValue(mesh, out var g)) return g;

            g = new MeshGeometry
            {
                Positions = mesh.vertices,
                SubMeshTriangles = new int[mesh.subMeshCount][],
                SubMeshTriangleOffset = new int[mesh.subMeshCount],
            };

            var offset = 0;
            for (var i = 0; i < mesh.subMeshCount; i++)
            {
                g.SubMeshTriangles[i] = mesh.GetTriangles(i);
                g.SubMeshTriangleOffset[i] = offset;
                offset += g.SubMeshTriangles[i].Length / 3;
            }

            g.TriangleArea = ComputeWorstCaseAreas(mesh, g, offset);
            _geometry[mesh] = g;
            return g;
        }

        /// <summary>
        /// EN: Triangle areas taking each blend shape at 0% and 100% (no combinations, no negative or
        ///     over-100 weights) and keeping the maximum per triangle.
        /// ZH: 每个形态键只取 0% 与 100%（不做组合、不考虑负值与超过 100 的情况），逐三角形取最大面积。
        /// </summary>
        private float[] ComputeWorstCaseAreas(Mesh mesh, MeshGeometry g, int triangleCount)
        {
            var areas = new float[triangleCount];
            var positions = g.Positions;

            AccumulateAreas(g, positions, areas);

            var shapeCount = mesh.blendShapeCount;
            if (shapeCount == 0) return areas;

            var vertexCount = mesh.vertexCount;
            var deltaV = new Vector3[vertexCount];
            var deltaN = new Vector3[vertexCount];
            var deltaT = new Vector3[vertexCount];
            var morphed = new Vector3[vertexCount];

            for (var s = 0; s < shapeCount; s++)
            {
                var frames = mesh.GetBlendShapeFrameCount(s);
                if (frames <= 0) continue;

                // EN: Only the 100% frame is relevant for the "0 or 100" rule. ZH: “0 或 100”规则只需要 100% 帧。
                mesh.GetBlendShapeFrameVertices(s, frames - 1, deltaV, deltaN, deltaT);
                for (var v = 0; v < vertexCount; v++) morphed[v] = positions[v] + deltaV[v];
                AccumulateAreas(g, morphed, areas);
            }

            _log.Trace("mesh", $"'{mesh.name}': worst case areas over {shapeCount} blend shapes");
            return areas;
        }

        private static void AccumulateAreas(MeshGeometry g, Vector3[] positions, float[] areas)
        {
            for (var sm = 0; sm < g.SubMeshTriangles.Length; sm++)
            {
                var tris = g.SubMeshTriangles[sm];
                var baseId = g.SubMeshTriangleOffset[sm];
                var count = tris.Length / 3;

                Parallel.For(0, count, t =>
                {
                    var i0 = tris[t * 3];
                    var i1 = tris[t * 3 + 1];
                    var i2 = tris[t * 3 + 2];
                    if (i0 >= positions.Length || i1 >= positions.Length || i2 >= positions.Length) return;

                    var area = Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]).magnitude *
                               0.5f;
                    var id = baseId + t;
                    if (area > areas[id]) areas[id] = area;
                });
            }
        }

        /// <summary>
        /// EN: Builds the island set of one UV key. <paramref name="scale"/> is the largest scale the
        ///     renderer can reach, used to convert local areas into world areas.
        /// ZH: 构建某个 UV 键的岛集合。<paramref name="scale"/> 是渲染器可达的最大缩放，用于把局部面积换算为世界面积。
        /// </summary>
        public ATOIslandSet Build(ATOUVKey key, Vector3 scale)
        {
            var mesh = key.Mesh;
            var set = new ATOIslandSet { Key = key };

            var uvList = new List<Vector2>();
            mesh.GetUVs(key.UVChannel, uvList);
            if (uvList.Count == 0)
            {
                _log.Trace("island", $"{key}: no UV data");
                return set;
            }

            var geometry = GetGeometry(mesh);
            if (key.SubMesh >= geometry.SubMeshTriangles.Length) return set;

            var tris = geometry.SubMeshTriangles[key.SubMesh];
            var triangleBase = geometry.SubMeshTriangleOffset[key.SubMesh];
            var uv = uvList.ToArray();
            set.NormalisedUV = uv;

            // EN: Union-find over triangles that share a UV vertex. ZH: 对共享 UV 顶点的三角形做并查集。
            var parent = new int[uv.Length];
            for (var i = 0; i < parent.Length; i++) parent[i] = i;

            // EN: Weld vertices that sit on the exact same UV so mirrored/split vertices stay in one island.
            // ZH: 把 UV 完全相同的顶点焊接起来，让镜像/拆分顶点留在同一个岛内。
            var weld = new Dictionary<long, int>(uv.Length);
            for (var i = 0; i < uv.Length; i++)
            {
                var hash = QuantizeUV(uv[i]);
                if (weld.TryGetValue(hash, out var other)) Union(parent, i, other);
                else weld[hash] = i;
            }

            for (var t = 0; t < tris.Length; t += 3)
            {
                Union(parent, tris[t], tris[t + 1]);
                Union(parent, tris[t + 1], tris[t + 2]);
            }

            // EN: Bucket triangles by island root. ZH: 按并查集根把三角形分桶。
            var byRoot = new Dictionary<int, List<int>>();
            for (var t = 0; t < tris.Length; t += 3)
            {
                var root = Find(parent, tris[t]);
                if (!byRoot.TryGetValue(root, out var list))
                {
                    list = new List<int>();
                    byRoot[root] = list;
                }

                list.Add(t / 3);
            }

            var index = 0;
            foreach (var kv in byRoot)
            {
                var island = BuildIsland(key, index++, kv.Value, tris, uv, geometry, triangleBase, scale, set);
                if (island != null) set.Islands.Add(island);
            }

            if (_mergeOverlapping) MergeOverlapping(set);

            _log.Trace("island",
                $"{key}: {set.Islands.Count} islands, crossesSeam={set.CrossesWrapSeam}");
            return set;
        }

        private ATOIsland BuildIsland(ATOUVKey key, int index, List<int> triangleIds, int[] tris, Vector2[] uv,
            MeshGeometry geometry, int triangleBase, Vector3 scale, ATOIslandSet set)
        {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            var vertexSet = new HashSet<int>();

            foreach (var t in triangleIds)
            {
                for (var k = 0; k < 3; k++)
                {
                    var vi = tris[t * 3 + k];
                    vertexSet.Add(vi);
                    var p = uv[vi];
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                }
            }

            // EN: Normalise a whole island that lies outside [0,1] but does not straddle a seam.
            // ZH: 把整体位于 [0,1] 之外但没有跨缝的岛平移归一化。
            var offset = new Vector2(Mathf.Floor(min.x + Epsilon), Mathf.Floor(min.y + Epsilon));
            var normMin = min - offset;
            var normMax = max - offset;

            if (normMax.x > 1f + Epsilon || normMax.y > 1f + Epsilon)
            {
                // EN: The island spans more than one tile, repeat sampling is required -> cannot repack.
                // ZH: 该岛跨越了多个 tile，需要 repeat 采样 -> 无法重排。
                set.CrossesWrapSeam = true;
                return null;
            }

            foreach (var vi in vertexSet) uv[vi] -= offset;

            var worldArea = 0f;
            foreach (var t in triangleIds) worldArea += geometry.TriangleArea[triangleBase + t];

            // EN: Area scales with the two dominant axes of the (possibly animated) scale.
            // ZH: 面积按（可能被动画修改的）缩放中两个最大的轴放大。
            var s = new[] { Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z) };
            Array.Sort(s);
            worldArea *= s[2] * s[1];

            var uvArea = 0f;
            foreach (var t in triangleIds)
            {
                var a = uv[tris[t * 3]];
                var b = uv[tris[t * 3 + 1]];
                var c = uv[tris[t * 3 + 2]];
                uvArea += Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) * 0.5f;
            }

            var triangleArray = new int[triangleIds.Count];
            triangleIds.CopyTo(triangleArray);

            var vertices = new int[vertexSet.Count];
            vertexSet.CopyTo(vertices);

            return new ATOIsland
            {
                Key = key,
                Index = index,
                Triangles = triangleArray,
                Vertices = vertices,
                Bounds = Rect.MinMaxRect(
                    Mathf.Clamp01(normMin.x), Mathf.Clamp01(normMin.y),
                    Mathf.Clamp01(normMax.x), Mathf.Clamp01(normMax.y)),
                WrapOffset = offset,
                WorldArea = worldArea,
                UVArea = uvArea,
            };
        }

        /// <summary>
        /// EN: Merges islands whose UV footprint is identical (mirrored / stacked UVs), so they share one
        ///     atlas slot instead of being duplicated.
        /// ZH: 合并 UV 覆盖范围完全一致的岛（镜像/叠放的 UV），让它们共用一个图集槽位而不是重复占位。
        /// </summary>
        private void MergeOverlapping(ATOIslandSet set)
        {
            var merged = 0;
            for (var i = 0; i < set.Islands.Count; i++)
            {
                var a = set.Islands[i];
                if (a == null) continue;

                for (var j = i + 1; j < set.Islands.Count; j++)
                {
                    var b = set.Islands[j];
                    if (b == null) continue;
                    if (!SameFootprint(a.Bounds, b.Bounds)) continue;

                    (a.Merged ??= new List<ATOIsland>()).Add(b);
                    b.Placement = default;
                    set.Islands[j] = null;
                    merged++;
                }
            }

            if (merged > 0)
            {
                set.Islands.RemoveAll(x => x == null);
                for (var i = 0; i < set.Islands.Count; i++) set.Islands[i].Index = i;
                _log.Trace("island", $"{set.Key}: merged {merged} overlapping islands");
            }
        }

        private static bool SameFootprint(Rect a, Rect b)
        {
            return Mathf.Abs(a.xMin - b.xMin) < 1e-4f && Mathf.Abs(a.yMin - b.yMin) < 1e-4f &&
                   Mathf.Abs(a.xMax - b.xMax) < 1e-4f && Mathf.Abs(a.yMax - b.yMax) < 1e-4f;
        }

        private static long QuantizeUV(Vector2 uv)
        {
            var x = (long)Mathf.RoundToInt(uv.x * 1048576f);
            var y = (long)Mathf.RoundToInt(uv.y * 1048576f);
            return (x << 32) ^ (y & 0xffffffffL);
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            var ra = Find(parent, a);
            var rb = Find(parent, b);
            if (ra != rb) parent[rb] = ra;
        }
    }
}
