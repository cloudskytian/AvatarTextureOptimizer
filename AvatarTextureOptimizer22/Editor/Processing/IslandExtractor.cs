// AvatarTextureOptimizer
// File: Editor/Processing/IslandExtractor.cs
//
// Extracts UV islands from each UV group's mesh + UV channel.
//   - triangles connected by shared UV vertices form islands (union-find)
//   - overlapping triangles (same UV region used by different mesh parts) are
//     merged into one island so remapping stays correct
//   - islands fully inside one wrap cell are normalized by whole-box
//     translation; islands that cross a wrap seam (repeat sampling) are
//     whitelisted with a warning
//   - per-island metadata: pixel bounds, solid-color flag, pixel density (px/m)
//   - blend-shape / animated-scale world area is taken at its maximum
//
// 从每个 UV 组的网格 + UV 通道提取 UV 岛。
//   - 通过共享 UV 顶点相连的三角形构成岛（并查集）
//   - 重叠三角形（不同网格部分占用同一 UV 区域）合并为一个岛，确保重映射
//     正确
//   - 完全位于一个 wrap 单元内的岛通过整体平移归一化；跨 wrap 缝（依赖
//     repeat 采样）的岛被白名单并给出警告
//   - 逐岛元数据：像素包围盒、纯色标志、像素密度（px/m）
//   - 形态键/动画缩放的面积取最大值

using System;
using System.Collections.Generic;
using net.fosa.avatar_texture_optimizer.editor.logging;
using net.fosa.avatar_texture_optimizer.editor.model;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.processing
{
    public static class IslandExtractor
    {
        // Epsilon for UV comparisons. / UV 比较的容差。
        private const float UvEps = 1e-5f;

        /// <summary>
        /// Extract islands for every UV group. UV groups without an accessible
        /// mesh are marked as skipped-atlas (whole-texture path).
        /// 为每个 UV 组提取岛。无法访问网格的 UV 组被标记为跳过图集化
        /// （整图路径）。
        /// </summary>
        public static void Extract(ATOBuildState state)
        {
            var stopwatch = new ATOStopwatch("IslandExtractor.Extract");
            int islandCount = 0;

            foreach (var group in state.UVGroups)
            {
                stopwatch.Begin($"group {group.Space}");
                var islands = ExtractForGroup(group, state);
                if (islands != null)
                {
                    group.Islands = islands;
                    islandCount += islands.Count;
                }
                stopwatch.End($"group {group.Space}");
            }

            ATOLog.Info($"[ATO] Extracted {islandCount} islands across {state.UVGroups.Count} UV groups. / 从 {state.UVGroups.Count} 个 UV 组提取了 {islandCount} 个岛。");
        }

        private static List<UVIsland> ExtractForGroup(UVGroup group, ATOBuildState state)
        {
            var renderer = group.Space.Renderer;
            if (renderer == null) return null;

            var mesh = GetMesh(renderer);
            if (mesh == null) return null;

            var uvChannel = group.Space.UVChannel;
            var uvs = ReadUVChannel(mesh, uvChannel);
            if (uvs == null || uvs.Count != mesh.vertexCount)
            {
                state.Warn($"[ATO] {group.Space}: UV channel {uvChannel} unavailable on mesh {mesh.name} -> skipped / UV 通道不可用，跳过");
                group.SkippedAtlas = true;
                return null;
            }

            int submeshIndex = Mathf.Clamp(group.Space.MaterialSlot, 0, mesh.subMeshCount - 1);
            var indices = mesh.GetIndices(submeshIndex);
            if (indices == null || indices.Length == 0) return null;

            var texture = group.Textures.Count > 0 ? group.Textures[0].Texture : null;
            group.Mesh = mesh;
            group.UVChannelData = uvs;
            group.SubmeshIndices = indices;
            int texW = texture != null ? texture.width : 1024;
            int texH = texture != null ? texture.height : 1024;

            // ---- 1. Connectivity + overlap via union-find ----
            // ---- 1. 通过并查集处理连通性 + 重叠 ----
            int triCount = indices.Length / 3;
            var uf = new UnionFind(triCount);

            // Spatial hash to find candidate neighbor triangles cheaply.
            // 空间哈希以便廉价地找到候选相邻三角形。
            var aabbs = new Rect[triCount];
            float gridCell = 0.02f; // UV-space cell size / UV 空间单元尺寸
            var hash = new SpatialHash(gridCell);

            for (int t = 0; t < triCount; t++)
            {
                var i0 = indices[t * 3];
                var i1 = indices[t * 3 + 1];
                var i2 = indices[t * 3 + 2];
                var u0 = uvs[i0]; var u1 = uvs[i1]; var u2 = uvs[i2];

                var min = Vector2.Min(u0, Vector2.Min(u1, u2));
                var max = Vector2.Max(u0, Vector2.Max(u1, u2));
                aabbs[t] = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
                hash.Add(t, (min + max) * 0.5f);
            }

            for (int t = 0; t < triCount; t++)
            {
                var i0 = indices[t * 3];
                var i1 = indices[t * 3 + 1];
                var i2 = indices[t * 3 + 2];
                var u0 = uvs[i0]; var u1 = uvs[i1]; var u2 = uvs[i2];

                var candidates = hash.Query(aabbs[t]);

                foreach (var o in candidates)
                {
                    if (o <= t) continue;

                    // Shared edge in UV space (two vertices with identical UVs).
                    // UV 空间中的共享边（两个顶点 UV 完全一致）。
                    bool connected = ShareUVEdge(u0, u1, u2, uvs[indices[o * 3]], uvs[indices[o * 3 + 1]], uvs[indices[o * 3 + 2]]);

                    // Overlap (different mesh parts using the same UV region).
                    // 重叠（不同网格部分使用同一 UV 区域）。
                    if (!connected && TrianglesOverlap2D(
                            u0, u1, u2,
                            uvs[indices[o * 3]], uvs[indices[o * 3 + 1]], uvs[indices[o * 3 + 2]]))
                    {
                        connected = true;
                    }

                    if (connected) uf.Union(t, o);
                }
            }

            // ---- 2. Build islands from union-find components ----
            // ---- 2. 从并查集分量构建岛 ----
            var components = new Dictionary<int, UVIsland>();
            var vertexToUV = new Dictionary<int, Vector2>(mesh.vertexCount);

            for (int v = 0; v < mesh.vertexCount; v++) vertexToUV[v] = uvs[v];

            for (int t = 0; t < triCount; t++)
            {
                int root = uf.Find(t);
                if (!components.TryGetValue(root, out var island))
                {
                    island = new UVIsland();
                    components[root] = island;
                }
                island.Triangles.Add(t);
                island.Vertices.Add(indices[t * 3]);
                island.Vertices.Add(indices[t * 3 + 1]);
                island.Vertices.Add(indices[t * 3 + 2]);
            }

            var result = new List<UVIsland>(components.Values);
            foreach (var island in result)
            {
                FinalizeIsland(island, uvs, texW, texH, mesh, renderer, submeshIndex, indices, state);
            }

            return result;
        }

        private static void FinalizeIsland(UVIsland island, List<Vector2> uvs, int texW, int texH,
            Mesh mesh, Renderer renderer, int submeshIndex, int[] submeshIndices, ATOBuildState state)
        {
            island.SubmeshIndex = submeshIndex;
            var uniqueVerts = new HashSet<int>(island.Vertices);

            // Rebuild vertices and their UVs in a consistent order (one UV per
            // vertex index). 以一致顺序重建顶点与其 UV（每个顶点索引一个 UV）。
            island.Vertices.Clear();
            island.UVs.Clear();
            foreach (var v in uniqueVerts)
            {
                island.Vertices.Add(v);
                island.UVs.Add(uvs[v]);
            }

            // Compute bounds in UV space. / 计算 UV 空间包围盒。
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            foreach (var u in island.UVs)
            {
                min = Vector2.Min(min, u);
                max = Vector2.Max(max, u);
            }
            island.BoundsUV = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            island.Centroid = new Vector2((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f);

            // Normalizability: all UVs inside a single wrap cell.
            // 可归一性：所有 UV 位于单个 wrap 单元内。
            bool normalizable = Mathf.Floor(min.x + UvEps) == Mathf.Floor(max.x - UvEps)
                             && Mathf.Floor(min.y + UvEps) == Mathf.Floor(max.y - UvEps);
            island.Normalizable = normalizable;

            if (!normalizable)
            {
                state.Warn($"[ATO] UV island at {island.BoundsUV} crosses a wrap seam (repeat sampling) -> whitelisted / UV 岛跨 wrap 缝（依赖 repeat 采样），视作白名单");
            }

            // Pixel bounds (outward-rounded). / 像素包围盒（向外取整）。
            int px0 = Mathf.FloorToInt(min.x * texW);
            int py0 = Mathf.FloorToInt(min.y * texH);
            int px1 = Mathf.CeilToInt(max.x * texW);
            int py1 = Mathf.CeilToInt(max.y * texH);
            island.PixelBounds = new RectInt(px0, py0, Mathf.Max(1, px1 - px0), Mathf.Max(1, py1 - py0));
            island.OriginalShortSide = Mathf.Min(island.PixelBounds.width, island.PixelBounds.height);

            // Solid-color detection is deferred to the scaler (it needs a
            // readable texture and is cheaper to check there once).
            // 纯色检测延迟到缩放器（它需要可读贴图，在那里检查一次更划算）。
            island.IsSolidColor = false;

            // Pixel density (px/m): world area at maximum deformation.
            // 像素密度（px/m）：最大形变下的世界面积。
            island.PixelDensityPPM = ComputePixelDensity(island, mesh, renderer, submeshIndices);

            island.RasterAreaPixels = island.BoundsAreaPixels;
        }

        private static bool ShareUVEdge(Vector2 a0, Vector2 a1, Vector2 a2, Vector2 b0, Vector2 b1, Vector2 b2)
        {
            // Two triangles are connected when two of their vertices share
            // identical UV coordinates (a shared edge in UV space).
            // 两个三角形在两个顶点共享相同 UV 坐标（UV 空间共享边）时连通。
            int shared = 0;
            if (NearAny(a0, b0, b1, b2)) shared++;
            if (NearAny(a1, b0, b1, b2)) shared++;
            if (NearAny(a2, b0, b1, b2)) shared++;
            return shared >= 2;
        }

        private static bool NearAny(Vector2 p, Vector2 b0, Vector2 b1, Vector2 b2)
        {
            return (p - b0).sqrMagnitude < UvEps || (p - b1).sqrMagnitude < UvEps || (p - b2).sqrMagnitude < UvEps;
        }

        /// <summary>Robust 2D triangle overlap test (edges crossing or containment). / 稳健的 2D 三角形重叠测试（边相交或包含）。</summary>
        private static bool TrianglesOverlap2D(Vector2 a0, Vector2 a1, Vector2 a2, Vector2 b0, Vector2 b1, Vector2 b2)
        {
            if (PointInTriangle(a0, b0, b1, b2) || PointInTriangle(b0, a0, a1, a2)) return true;
            return SegmentsIntersect(a0, a1, b0, b1) || SegmentsIntersect(a0, a1, b1, b2) ||
                   SegmentsIntersect(a0, a1, b2, b0) ||
                   SegmentsIntersect(a1, a2, b0, b1) || SegmentsIntersect(a1, a2, b1, b2) ||
                   SegmentsIntersect(a1, a2, b2, b0) ||
                   SegmentsIntersect(a2, a0, b0, b1) || SegmentsIntersect(a2, a0, b1, b2) ||
                   SegmentsIntersect(a2, a0, b2, b0);
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(b - a, p - a);
            float d2 = Cross(c - b, p - b);
            float d3 = Cross(a - c, p - c);
            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNeg && hasPos);
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
        {
            float d1 = Cross(q2 - q1, p1 - q1);
            float d2 = Cross(q2 - q1, p2 - q1);
            float d3 = Cross(p2 - p1, q1 - p1);
            float d4 = Cross(p2 - p1, q2 - p1);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static float ComputePixelDensity(UVIsland island, Mesh mesh, Renderer renderer, int[] submeshIndices)
        {
            // World area of the island's triangles (approx.: uses transform
            // scale; skinned deformations are approximated by scale — blend
            // shape maxima are folded in by the scaler's area analysis later).
            // 岛三角形的世界面积（近似：使用变换缩放；蒙皮形变以缩放近似——
            // 形态键最大值由缩放器后续的面积分析并入）。
            var l2w = renderer.transform.localToWorldMatrix;
            var vertices = mesh.vertices;
            double worldArea = 0;
            foreach (var t in island.Triangles)
            {
                int baseIdx = t * 3;
                if (baseIdx + 2 >= submeshIndices.Length) continue;
                var p0 = l2w.MultiplyPoint3x4(vertices[submeshIndices[baseIdx]]);
                var p1 = l2w.MultiplyPoint3x4(vertices[submeshIndices[baseIdx + 1]]);
                var p2 = l2w.MultiplyPoint3x4(vertices[submeshIndices[baseIdx + 2]]);
                worldArea += 0.5 * Vector3.Cross(p1 - p0, p2 - p0).magnitude;
            }
            if (worldArea <= 1e-9) return -1f;

            // pixels per meter = sqrt(pixelArea / worldArea)
            // 每米像素 = sqrt(像素面积 / 世界面积)
            double pixelArea = island.BoundsAreaPixels;
            double ppm = Math.Sqrt(pixelArea / worldArea);
            return (float)ppm;
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            switch (renderer)
            {
                case SkinnedMeshRenderer smr: return smr.sharedMesh;
                case MeshRenderer mr:
                    var mf = mr.GetComponent<MeshFilter>();
                    return mf != null ? mf.sharedMesh : null;
                default: return null;
            }
        }

        private static List<Vector2> ReadUVChannel(Mesh mesh, int channel)
        {
            var list = new List<Vector2>();
            try
            {
                mesh.GetUVs(channel, list);
            }
            catch
            {
                return null;
            }
            return list;
        }

        // ---- Union-find / 并查集 ----
        private sealed class UnionFind
        {
            private readonly int[] _parent;
            public UnionFind(int n) { _parent = new int[n]; for (int i = 0; i < n; i++) _parent[i] = i; }
            public int Find(int x)
            {
                while (_parent[x] != x)
                {
                    _parent[x] = _parent[_parent[x]];
                    x = _parent[x];
                }
                return x;
            }
            public void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) _parent[ra] = rb;
            }
        }

        // ---- Spatial hash for triangle locality / 三角形局部性空间哈希 ----
        private sealed class SpatialHash
        {
            private readonly float _cell;
            private readonly Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();
            public SpatialHash(float cell) { _cell = Mathf.Max(1e-4f, cell); }
            private long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;
            public void Add(int index, Vector2 center)
            {
                int cx = Mathf.FloorToInt(center.x / _cell);
                int cy = Mathf.FloorToInt(center.y / _cell);
                long key = Key(cx, cy);
                if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<int>();
                list.Add(index);
            }
            public List<int> Query(Rect bounds)
            {
                var result = new List<int>();
                int x0 = Mathf.FloorToInt(bounds.xMin / _cell);
                int x1 = Mathf.FloorToInt(bounds.xMax / _cell);
                int y0 = Mathf.FloorToInt(bounds.yMin / _cell);
                int y1 = Mathf.FloorToInt(bounds.yMax / _cell);
                for (int cx = x0; cx <= x1; cx++)
                for (int cy = y0; cy <= y1; cy++)
                {
                    if (_grid.TryGetValue(Key(cx, cy), out var list))
                        result.AddRange(list);
                }
                return result;
            }
        }
    }
}
