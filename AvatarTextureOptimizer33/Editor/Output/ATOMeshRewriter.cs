// SPDX-License-Identifier: MIT
// EN: Rewrites mesh UVs to the atlas layout, duplicating vertices only where sub meshes share them.
// ZH: 把网格 UV 重写到图集布局；仅在子网格共享顶点时才复制顶点。

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: One UV rewrite instruction: which islands of which (sub mesh, channel) go where.
    /// ZH: 一条 UV 重写指令：某个（子网格, 通道）的哪些岛去往哪里。
    /// </summary>
    public sealed class ATOUVRewrite
    {
        public ATOUVKey Key;
        public Vector2[] NormalisedUV;
        public List<ATOIsland> Islands = new List<ATOIsland>();
        public int LayoutWidth;
        public int LayoutHeight;
    }

    /// <summary>
    /// EN: Rebuilds meshes with atlas UVs.
    /// ZH: 用图集 UV 重建网格。
    /// </summary>
    public sealed class ATOMeshRewriter
    {
        private readonly ATOLog _log;
        private readonly Dictionary<Mesh, Mesh> _rewritten = new Dictionary<Mesh, Mesh>();

        public ATOMeshRewriter(ATOLog log)
        {
            _log = log;
        }

        /// <summary>EN: original mesh -&gt; rewritten mesh. ZH: 原网格 -&gt; 重写后的网格。</summary>
        public IReadOnlyDictionary<Mesh, Mesh> Result => _rewritten;

        /// <summary>
        /// EN: Rewrites one mesh. <paramref name="rewrites"/> must all belong to that mesh.
        /// ZH: 重写一个网格。<paramref name="rewrites"/> 必须全部属于该网格。
        /// </summary>
        public Mesh Rewrite(Mesh mesh, List<ATOUVRewrite> rewrites)
        {
            if (mesh == null || rewrites == null || rewrites.Count == 0) return mesh;
            if (_rewritten.TryGetValue(mesh, out var cached)) return cached;

            var source = new MeshData(mesh);
            var duplicates = FindCrossSubMeshVertices(source, rewrites);

            var newUVs = new Dictionary<int, Vector2[]>();
            foreach (var rewrite in rewrites)
            {
                if (!newUVs.TryGetValue(rewrite.Key.UVChannel, out var uvArray))
                {
                    uvArray = source.GetUV(rewrite.Key.UVChannel);
                    if (uvArray == null || uvArray.Length == 0)
                    {
                        _log.Warning("mesh", $"'{mesh.name}': UV{rewrite.Key.UVChannel} missing, skipped");
                        continue;
                    }

                    uvArray = (Vector2[])uvArray.Clone();
                    newUVs[rewrite.Key.UVChannel] = uvArray;
                }
            }

            // EN: With duplicated vertices each sub mesh gets its own copy of the shared vertices.
            // ZH: 需要复制顶点时，每个子网格都会得到共享顶点的独立副本。
            var builder = new MeshBuilder(source, duplicates);

            foreach (var rewrite in rewrites)
            {
                if (!newUVs.TryGetValue(rewrite.Key.UVChannel, out var uvArray)) continue;

                foreach (var island in rewrite.Islands)
                {
                    ApplyIsland(builder, rewrite, island, uvArray);
                    if (island.Merged == null) continue;
                    foreach (var merged in island.Merged)
                    {
                        merged.Placement = island.Placement;
                        ApplyIsland(builder, rewrite, merged, uvArray);
                    }
                }
            }

            var result = builder.Build(mesh, newUVs, rewrites);
            _rewritten[mesh] = result;
            _log.Info("mesh",
                $"'{mesh.name}' rewritten: {rewrites.Count} UV streams, vertices {source.VertexCount} -> {builder.VertexCount}");
            return result;
        }

        private void ApplyIsland(MeshBuilder builder, ATOUVRewrite rewrite, ATOIsland island, Vector2[] uvArray)
        {
            var placement = island.Placement;
            if (!placement.Valid) return;

            var bounds = island.Bounds;
            var invW = 1f / Mathf.Max(1e-6f, bounds.width);
            var invH = 1f / Mathf.Max(1e-6f, bounds.height);
            var layoutW = Mathf.Max(1, rewrite.LayoutWidth);
            var layoutH = Mathf.Max(1, rewrite.LayoutHeight);

            foreach (var vi in island.Vertices)
            {
                var src = rewrite.NormalisedUV[vi];
                var u = (src.x - bounds.xMin) * invW;
                var v = (src.y - bounds.yMin) * invH;

                float px, py;
                if (placement.Rotated)
                {
                    px = placement.X + (1f - v) * placement.Width;
                    py = placement.Y + u * placement.Height;
                }
                else
                {
                    px = placement.X + u * placement.Width;
                    py = placement.Y + v * placement.Height;
                }

                var mapped = new Vector2(px / layoutW, py / layoutH);
                builder.SetUV(rewrite.Key.SubMesh, vi, rewrite.Key.UVChannel, mapped, uvArray);
            }
        }

        private static HashSet<int> FindCrossSubMeshVertices(MeshData mesh, List<ATOUVRewrite> rewrites)
        {
            var touched = new HashSet<int>();
            var shared = new HashSet<int>();
            var owner = new Dictionary<int, int>();

            foreach (var rewrite in rewrites)
            {
                var sm = rewrite.Key.SubMesh;
                if (sm < 0 || sm >= mesh.SubMeshTriangles.Length) continue;

                foreach (var vi in mesh.SubMeshTriangles[sm])
                {
                    touched.Add(vi);
                    if (owner.TryGetValue(vi, out var existing))
                    {
                        if (existing != sm) shared.Add(vi);
                    }
                    else
                    {
                        owner[vi] = sm;
                    }
                }
            }

            // EN: Vertices also used by sub meshes that are not rewritten must be duplicated as well.
            // ZH: 同时被未参与重写的子网格使用的顶点，同样需要复制。
            for (var sm = 0; sm < mesh.SubMeshTriangles.Length; sm++)
            {
                var isRewritten = false;
                foreach (var r in rewrites)
                    if (r.Key.SubMesh == sm)
                        isRewritten = true;
                if (isRewritten) continue;

                foreach (var vi in mesh.SubMeshTriangles[sm])
                    if (touched.Contains(vi))
                        shared.Add(vi);
            }

            return shared;
        }

        // ------------------------------------------------------------------ mesh data

        private sealed class MeshData
        {
            public readonly Vector3[] Vertices;
            public readonly Vector3[] Normals;
            public readonly Vector4[] Tangents;
            public readonly Color32[] Colors;
            public readonly Vector2[][] UVs = new Vector2[8][];
            public readonly BoneWeight[] BoneWeights;
            public readonly Matrix4x4[] BindPoses;
            public readonly int[][] SubMeshTriangles;
            public readonly MeshTopology[] Topologies;
            public int VertexCount => Vertices.Length;

            public MeshData(Mesh mesh)
            {
                Vertices = mesh.vertices;
                Normals = mesh.normals;
                Tangents = mesh.tangents;
                Colors = mesh.colors32;
                BoneWeights = mesh.boneWeights;
                BindPoses = mesh.bindposes;

                for (var i = 0; i < 8; i++)
                {
                    var list = new List<Vector2>();
                    mesh.GetUVs(i, list);
                    UVs[i] = list.Count > 0 ? list.ToArray() : null;
                }

                SubMeshTriangles = new int[mesh.subMeshCount][];
                Topologies = new MeshTopology[mesh.subMeshCount];
                for (var i = 0; i < mesh.subMeshCount; i++)
                {
                    SubMeshTriangles[i] = mesh.GetTriangles(i);
                    Topologies[i] = mesh.GetTopology(i);
                }
            }

            public Vector2[] GetUV(int channel) => channel >= 0 && channel < 8 ? UVs[channel] : null;
        }

        /// <summary>
        /// EN: Builds the output mesh, duplicating the vertices that several sub meshes disagree about.
        /// ZH: 构建输出网格，对多个子网格存在分歧的顶点进行复制。
        /// </summary>
        private sealed class MeshBuilder
        {
            private readonly MeshData _source;
            private readonly Dictionary<(int subMesh, int vertex), int> _map =
                new Dictionary<(int, int), int>();

            private readonly List<int> _originalIndex = new List<int>();
            private readonly HashSet<int> _duplicated;

            public MeshBuilder(MeshData source, HashSet<int> duplicated)
            {
                _source = source;
                _duplicated = duplicated;

                for (var i = 0; i < source.VertexCount; i++) _originalIndex.Add(i);

                // EN: Materialise every duplicate up front so UV assignment and index remapping agree.
                // ZH: 提前创建全部副本，保证 UV 赋值与索引重映射一致。
                for (var sm = 0; sm < source.SubMeshTriangles.Length; sm++)
                foreach (var vi in source.SubMeshTriangles[sm])
                    if (_duplicated.Contains(vi))
                        Resolve(sm, vi);
            }

            public int VertexCount => _originalIndex.Count;

            public int Resolve(int subMesh, int vertex)
            {
                if (!_duplicated.Contains(vertex)) return vertex;
                if (_map.TryGetValue((subMesh, vertex), out var mapped)) return mapped;

                mapped = _originalIndex.Count;
                _originalIndex.Add(vertex);
                _map[(subMesh, vertex)] = mapped;
                return mapped;
            }

            public void SetUV(int subMesh, int vertex, int channel, Vector2 value, Vector2[] uvArray)
            {
                var index = Resolve(subMesh, vertex);
                if (index < uvArray.Length)
                {
                    uvArray[index] = value;
                    return;
                }

                // EN: The array grows lazily together with the duplicated vertices.
                // ZH: 数组随着复制顶点一起惰性增长。
                PendingUV.Add((channel, index, value));
            }

            public readonly List<(int channel, int index, Vector2 value)> PendingUV =
                new List<(int, int, Vector2)>();

            public Mesh Build(Mesh original, Dictionary<int, Vector2[]> newUVs, List<ATOUVRewrite> rewrites)
            {
                var count = _originalIndex.Count;
                var mesh = new Mesh
                {
                    name = original.name + "_ATO",
                    indexFormat = count > 65000 ? IndexFormat.UInt32 : original.indexFormat,
                };

                var vertices = new Vector3[count];
                for (var i = 0; i < count; i++) vertices[i] = _source.Vertices[_originalIndex[i]];
                mesh.vertices = vertices;

                if (_source.Normals != null && _source.Normals.Length == _source.VertexCount)
                {
                    var normals = new Vector3[count];
                    for (var i = 0; i < count; i++) normals[i] = _source.Normals[_originalIndex[i]];
                    mesh.normals = normals;
                }

                if (_source.Tangents != null && _source.Tangents.Length == _source.VertexCount)
                {
                    // EN: Tangents are copied verbatim, never recomputed. ZH: 切线原样拷贝，绝不重算。
                    var tangents = new Vector4[count];
                    for (var i = 0; i < count; i++) tangents[i] = _source.Tangents[_originalIndex[i]];
                    mesh.tangents = tangents;
                }

                if (_source.Colors != null && _source.Colors.Length == _source.VertexCount)
                {
                    var colors = new Color32[count];
                    for (var i = 0; i < count; i++) colors[i] = _source.Colors[_originalIndex[i]];
                    mesh.colors32 = colors;
                }

                for (var channel = 0; channel < 8; channel++)
                {
                    var srcUV = newUVs.TryGetValue(channel, out var rewritten) ? rewritten : _source.UVs[channel];
                    if (srcUV == null || srcUV.Length == 0) continue;

                    var uv = new Vector2[count];
                    for (var i = 0; i < count; i++)
                    {
                        var oi = _originalIndex[i];
                        uv[i] = oi < srcUV.Length ? srcUV[oi] : Vector2.zero;
                    }

                    foreach (var pending in PendingUV)
                        if (pending.channel == channel && pending.index < count)
                            uv[pending.index] = pending.value;

                    mesh.SetUVs(channel, new List<Vector2>(uv));
                }

                if (_source.BoneWeights != null && _source.BoneWeights.Length == _source.VertexCount)
                {
                    var weights = new BoneWeight[count];
                    for (var i = 0; i < count; i++) weights[i] = _source.BoneWeights[_originalIndex[i]];
                    mesh.boneWeights = weights;
                }

                if (_source.BindPoses != null && _source.BindPoses.Length > 0) mesh.bindposes = _source.BindPoses;

                mesh.subMeshCount = _source.SubMeshTriangles.Length;
                for (var sm = 0; sm < _source.SubMeshTriangles.Length; sm++)
                {
                    var tris = _source.SubMeshTriangles[sm];
                    var remapped = new int[tris.Length];
                    for (var i = 0; i < tris.Length; i++) remapped[i] = Resolve(sm, tris[i]);
                    mesh.SetTriangles(remapped, sm, true);
                }

                CopyBlendShapes(original, mesh, count);

                mesh.RecalculateBounds();
                return mesh;
            }

            private void CopyBlendShapes(Mesh original, Mesh mesh, int count)
            {
                var vertexCount = _source.VertexCount;
                if (original.blendShapeCount == 0) return;

                var dv = new Vector3[vertexCount];
                var dn = new Vector3[vertexCount];
                var dt = new Vector3[vertexCount];

                var ov = new Vector3[count];
                var on = new Vector3[count];
                var ot = new Vector3[count];

                for (var s = 0; s < original.blendShapeCount; s++)
                {
                    var name = original.GetBlendShapeName(s);
                    var frames = original.GetBlendShapeFrameCount(s);
                    for (var f = 0; f < frames; f++)
                    {
                        var weight = original.GetBlendShapeFrameWeight(s, f);
                        original.GetBlendShapeFrameVertices(s, f, dv, dn, dt);

                        for (var i = 0; i < count; i++)
                        {
                            var oi = _originalIndex[i];
                            ov[i] = dv[oi];
                            on[i] = dn[oi];
                            ot[i] = dt[oi];
                        }

                        mesh.AddBlendShapeFrame(name, weight, ov, on, ot);
                    }
                }
            }
        }
    }
}
