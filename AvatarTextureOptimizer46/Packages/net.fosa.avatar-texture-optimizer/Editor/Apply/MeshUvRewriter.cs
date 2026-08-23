// SPDX-License-Identifier: MIT
// EN: Rewrites a mesh UV channel from reference texture space to atlas space, splitting vertices that
//     end up needing two different atlas coordinates.
// ZH: 将网格的某个 UV 通道从参考贴图空间重写到图集空间，并拆分那些最终需要两套不同图集坐标的顶点。

using System;
using System.Collections.Generic;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Model;
using Net.Fosa.AvatarTextureOptimizer.Editor.Plugin;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Apply
{
    /// <summary>
    /// EN: Accumulates rewrite plans for one mesh and one UV channel, then applies them in a single pass.
    /// ZH: 为一个网格的一个 UV 通道累积重写计划，然后一次性应用。
    /// </summary>
    public sealed class MeshUvRewriter
    {
        private const string Stage = "Apply";

        private readonly Mesh _mesh;
        private readonly int _channel;
        private readonly List<(UvRewritePlan plan, Vector2Int atlasSize)> _plans = new List<(UvRewritePlan, Vector2Int)>();

        /// <summary>EN: Creates a rewriter. ZH: 创建重写器。</summary>
        public MeshUvRewriter(Mesh mesh, int channel)
        {
            _mesh = mesh;
            _channel = channel;
        }

        /// <summary>EN: Queues one plan. ZH: 加入一个计划。</summary>
        public void AddPlan(UvRewritePlan plan, Vector2Int atlasSize) => _plans.Add((plan, atlasSize));

        /// <summary>
        /// EN: Applies every queued plan. Vertices are duplicated when a single vertex is referenced by
        ///     triangles that landed in different islands, because a vertex can only carry one UV.
        /// ZH: 应用所有已加入的计划。当同一个顶点被落在不同岛的三角形引用时会复制顶点，
        ///     因为一个顶点只能携带一组 UV。
        /// </summary>
        public void Commit()
        {
            if (_plans.Count == 0) return;

            var uvs = new List<Vector2>();
            _mesh.GetUVs(_channel, uvs);
            if (uvs.Count != _mesh.vertexCount)
            {
                AtoLog.Warning(Stage, $"mesh '{_mesh.name}' UV{_channel} size mismatch; skipping rewrite.");
                return;
            }

            var splitter = new VertexSplitter(_mesh);
            var newUvs = new List<Vector2>(uvs);

            foreach (var (plan, atlasSize) in _plans)
            {
                var group = plan.Group;
                var indices = _mesh.GetTriangles(plan.Slot.SubMesh);
                var updated = (int[])indices.Clone();

                foreach (var tri in plan.Triangles)
                {
                    if (tri.IslandIndex < 0 || tri.IslandIndex >= group.Islands.Count) continue;
                    var island = group.Islands[tri.IslandIndex];
                    if (island.AtlasIndex == -1) continue;

                    int baseIdx = tri.TriangleIndex * 3;
                    if (baseIdx + 2 >= updated.Length) continue;

                    for (int k = 0; k < 3; k++)
                    {
                        int vertexIndex = updated[baseIdx + k];
                        var sourceUv = uvs[vertexIndex] + plan.Shift;
                        var target = MapToAtlas(sourceUv, island, group.ReferenceSize, atlasSize);

                        int finalIndex = splitter.Resolve(vertexIndex, target, newUvs);
                        updated[baseIdx + k] = finalIndex;
                    }
                }

                splitter.PendingTriangles[plan.Slot.SubMesh] = updated;
            }

            splitter.Apply(_mesh, _channel, newUvs);
            AtoLog.Debug_(Stage, $"mesh '{_mesh.name}' UV{_channel}: {splitter.SplitCount} vertices split");
        }

        /// <summary>
        /// EN: Delegates to <see cref="AtlasUvMapping.MapToAtlas"/>; kept as a thin forwarder so existing
        ///     call sites and third party extensions do not have to change.
        /// ZH: 转发到 <see cref="AtlasUvMapping.MapToAtlas"/>；保留为薄封装，
        ///     使现有调用点与第三方扩展无需改动。
        /// </summary>
        public static Vector2 MapToAtlas(Vector2 uv, UvIsland island, Vector2Int referenceSize, Vector2Int atlasSize)
            => AtlasUvMapping.MapToAtlas(uv, island, referenceSize, atlasSize);
    }

    /// <summary>
    /// EN: Duplicates vertices on demand so that each vertex carries exactly one atlas UV.
    /// ZH: 按需复制顶点，使每个顶点恰好携带一组图集 UV。
    /// </summary>
    internal sealed class VertexSplitter
    {
        private const string Stage = "Apply";

        private readonly Mesh _mesh;
        private readonly Dictionary<(int, Vector2), int> _map = new Dictionary<(int, Vector2), int>();
        // EN: Tracks which original vertices already carry a rewritten UV. A HashSet keeps Resolve O(1);
        //     scanning the key set instead would make the whole rewrite quadratic in vertex count.
        // ZH: 记录哪些原始顶点已经携带了重写后的 UV。用 HashSet 让 Resolve 保持 O(1)；
        //     若改为扫描键集合，整个重写会退化为顶点数的平方复杂度。
        private readonly HashSet<int> _assigned = new HashSet<int>();
        private readonly List<int> _sourceOf = new List<int>();

        /// <summary>EN: Rewritten triangle lists per sub mesh. ZH: 每个子网格重写后的三角形列表。</summary>
        public readonly Dictionary<int, int[]> PendingTriangles = new Dictionary<int, int[]>();
        /// <summary>EN: How many vertices had to be duplicated. ZH: 需要复制的顶点数量。</summary>
        public int SplitCount { get; private set; }

        public VertexSplitter(Mesh mesh)
        {
            _mesh = mesh;
            for (int i = 0; i < mesh.vertexCount; i++) _sourceOf.Add(i);
        }

        /// <summary>
        /// EN: Returns the vertex index that carries <paramref name="uv"/>, creating a duplicate when the
        ///     original already carries a different coordinate.
        /// ZH: 返回携带 <paramref name="uv"/> 的顶点索引；当原顶点已携带不同坐标时创建副本。
        /// </summary>
        public int Resolve(int vertexIndex, Vector2 uv, List<Vector2> uvs)
        {
            var rounded = new Vector2(Mathf.Round(uv.x * 65536f) / 65536f, Mathf.Round(uv.y * 65536f) / 65536f);
            var key = (vertexIndex, rounded);
            if (_map.TryGetValue(key, out var existing)) return existing;

            if (_assigned.Add(vertexIndex))
            {
                // EN: First assignment for this vertex - reuse it in place.
                // ZH: 该顶点的首次赋值 —— 就地复用。
                uvs[vertexIndex] = rounded;
                _map[key] = vertexIndex;
                return vertexIndex;
            }

            int newIndex = uvs.Count;
            uvs.Add(rounded);
            _sourceOf.Add(_sourceOf[vertexIndex]);
            _map[key] = newIndex;
            SplitCount++;
            return newIndex;
        }

        /// <summary>
        /// EN: Writes the duplicated vertex attributes and the rewritten UVs back into the mesh.
        ///     Every sub mesh is rewritten, not only the ones that were remapped: growing the vertex
        ///     buffer invalidates the index buffers, so they all have to be re-uploaded.
        /// ZH: 将复制出的顶点属性与重写后的 UV 写回网格。
        ///     所有子网格都会被重写，而不只是被重映射的那些：顶点缓冲增长会使索引缓冲失效，
        ///     因此必须全部重新上传。
        /// </summary>
        public void Apply(Mesh mesh, int channel, List<Vector2> uvs)
        {
            // EN: Snapshot every sub mesh before touching the vertex buffer, applying the rewritten
            //     index lists where we have them.
            // ZH: 在改动顶点缓冲之前先快照每个子网格，并对已有重写结果的子网格套用新的索引表。
            int subMeshCount = mesh.subMeshCount;
            var triangles = new int[subMeshCount][];
            var topologies = new MeshTopology[subMeshCount];
            for (int i = 0; i < subMeshCount; i++)
            {
                topologies[i] = mesh.GetTopology(i);
                triangles[i] = PendingTriangles.TryGetValue(i, out var rewritten)
                    ? rewritten
                    : mesh.GetTriangles(i);
            }

            if (uvs.Count > mesh.vertexCount)
                GrowVertexBuffer(mesh, uvs.Count, triangles);

            mesh.SetUVs(channel, uvs);

            for (int i = 0; i < subMeshCount; i++)
            {
                if (topologies[i] != MeshTopology.Triangles)
                {
                    // EN: Non triangle topologies are left exactly as they were.
                    // ZH: 非三角形拓扑保持原样。
                    continue;
                }
                mesh.SetTriangles(triangles[i], i, false);
            }

            mesh.RecalculateBounds();
        }

        /// <summary>
        /// EN: Grows every per vertex stream to <paramref name="newCount"/> entries by duplicating the
        ///     source vertex of each added entry. The mesh is never cleared, so bind poses, the bounds and
        ///     any stream we do not touch survive untouched.
        /// ZH: 通过复制每个新增条目的源顶点，把所有逐顶点数据流增长到 <paramref name="newCount"/> 个条目。
        ///     全程不清空网格，因此 bindposes、包围盒以及任何未触碰的数据流都原样保留。
        /// </summary>
        private void GrowVertexBuffer(Mesh mesh, int newCount, int[][] triangles)
        {
            int old = mesh.vertexCount;
            if (newCount <= old) return;

            T[] Grow<T>(T[] src)
            {
                if (src == null || src.Length != old) return null;
                var dst = new T[newCount];
                Array.Copy(src, dst, old);
                for (int i = old; i < newCount; i++) dst[i] = src[_sourceOf[i]];
                return dst;
            }

            // EN: Capture blend shapes before the vertex count changes; their delta arrays must match it.
            // ZH: 在顶点数改变之前先捕获形态键；它们的 delta 数组必须与顶点数一致。
            var shapes = new List<(string name, float weight, Vector3[] dv, Vector3[] dn, Vector3[] dt)>();
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                var name = mesh.GetBlendShapeName(s);
                for (int f = 0; f < mesh.GetBlendShapeFrameCount(s); f++)
                {
                    var dv = new Vector3[old];
                    var dn = new Vector3[old];
                    var dt = new Vector3[old];
                    mesh.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                    shapes.Add((name, mesh.GetBlendShapeFrameWeight(s, f), Grow(dv), Grow(dn), Grow(dt)));
                }
            }

            var normals = Grow(mesh.normals);
            var tangents = Grow(mesh.tangents);
            var colors = Grow(mesh.colors32);
            var boneWeights = Grow(mesh.boneWeights);
            var extraUvs = new List<Vector2>[8];
            for (int c = 0; c < 8; c++)
            {
                var list = new List<Vector2>();
                mesh.GetUVs(c, list);
                if (list.Count != old) continue;
                for (int i = old; i < newCount; i++) list.Add(list[_sourceOf[i]]);
                extraUvs[c] = list;
            }

            // EN: Index buffers must be emptied first: while the vertex array is being replaced Unity
            //     validates indices against the new count, and a stale larger index would throw.
            // ZH: 必须先清空索引缓冲：替换顶点数组期间 Unity 会按新顶点数校验索引，
            //     残留的越界索引会抛异常。
            for (int i = 0; i < mesh.subMeshCount; i++)
                mesh.SetTriangles(Array.Empty<int>(), i, false);

            mesh.vertices = Grow(mesh.vertices);
            if (normals != null) mesh.normals = normals;
            if (tangents != null) mesh.tangents = tangents;
            if (colors != null) mesh.colors32 = colors;
            if (boneWeights != null) mesh.boneWeights = boneWeights;
            for (int c = 0; c < 8; c++)
                if (extraUvs[c] != null)
                    mesh.SetUVs(c, extraUvs[c]);

            mesh.ClearBlendShapes();
            foreach (var (name, weight, dv, dn, dt) in shapes)
                mesh.AddBlendShapeFrame(name, weight, dv, dn, dt);

            AtoLog.Debug_(Stage, $"mesh '{mesh.name}': vertex buffer grown {old} -> {newCount}");
        }
    }
}
