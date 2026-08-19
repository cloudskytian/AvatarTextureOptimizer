// UV Island Extractor - Complete with overlapping island merge, triangle UV data
// UV岛提取器 - 包含重叠岛合并、三角形UV数据

using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Processing
{
    public static class UVIslandExtractor
    {
        public static List<UVIsland> ExtractIslands(
            Mesh mesh, int uvChannel, List<Vector2> uvs,
            Renderer renderer, ATOBuildContext atoCtx, ref int nextIslandId)
        {
            var islands = new List<UVIsland>();
            if (mesh == null || uvs == null || uvs.Count == 0) return islands;

            var vertices = mesh.vertices;

            for (int subMeshIdx = 0; subMeshIdx < mesh.subMeshCount; subMeshIdx++)
            {
                var triangles = mesh.GetTriangles(subMeshIdx);
                if (triangles.Length == 0) continue;

                // Group triangles into connected UV islands (Union-Find)
                var triangleGroups = GroupTrianglesIntoIslands(triangles, uvs);

                foreach (var group in triangleGroups)
                {
                    var island = new UVIsland
                    {
                        Id = nextIslandId++,
                        SourceMesh = mesh,
                        SubMeshIndex = subMeshIdx,
                        UvChannel = uvChannel,
                        TriangleIndices = group
                    };

                    // Collect unique vertex indices and their UVs
                    var vertSet = new HashSet<int>();
                    foreach (int vi in group) if (vi < uvs.Count) vertSet.Add(vi);
                    var vertList = vertSet.OrderBy(v => v).ToList();
                    island.UVs = vertList.Select(v => uvs[v]).ToList();

                    // Store precise triangle UV data for rasterization
                    // 存储精确三角形UV数据用于光栅化
                    for (int i = 0; i < group.Count; i += 3)
                    {
                        if (i + 2 < group.Count)
                        {
                            int a = group[i], b = group[i + 1], c = group[i + 2];
                            if (a < uvs.Count && b < uvs.Count && c < uvs.Count)
                            {
                                island.TrianglesUV.Add(new TriangleUV
                                {
                                    V0 = uvs[a], V1 = uvs[b], V2 = uvs[c]
                                });
                            }
                        }
                    }

                    // Calculate UV bounds
                    var bounds = TextureHelper.GetUVBounds(island.UVs);
                    island.BoundsMin = bounds.min;
                    island.BoundsMax = bounds.max;

                    // Calculate UV area (from actual triangles)
                    float uvArea = 0;
                    foreach (var tri in island.TrianglesUV)
                        uvArea += TextureHelper.CalculateUVTriangleArea(tri.V0, tri.V1, tri.V2);
                    island.UVArea = uvArea;

                    // Physical area with animation scale + blend shapes
                    island.PhysicalArea = CalculatePhysicalArea(mesh, group, vertices, renderer, atoCtx);

                    // Normalize out-of-bounds UVs
                    NormalizeUVBounds(island, atoCtx);

                    // Evaluate blend shapes
                    EvaluateBlendShapes(mesh, group, island);

                    // Check for pure color
                    CheckPureColor(island, atoCtx);

                    // Find source texture
                    island.SourceTextureIndex = FindSourceTextureIndex(renderer, subMeshIdx, uvChannel, atoCtx);

                    // Check whitelist
                    if (island.SourceTextureIndex >= 0 && island.SourceTextureIndex < atoCtx.AllTextures.Count)
                        island.IsWhitelisted = atoCtx.AllTextures[island.SourceTextureIndex].IsWhitelisted;

                    islands.Add(island);
                }
            }

            // Merge overlapping islands within the same UV space
            // 合并同一UV空间内的重叠岛
            islands = MergeOverlappingIslands(islands);

            return islands;
        }

        /// <summary>
        /// Merge islands that have overlapping UV regions (same UV, different submeshes).
        /// 合并UV区域重叠的岛（相同UV，不同子网格）。
        /// </summary>
        private static List<UVIsland> MergeOverlappingIslands(List<UVIsland> islands)
        {
            if (islands.Count <= 1) return islands;

            // Use Union-Find on islands that share UV space
            int n = islands.Count;
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

            // Check bounding box overlap (quick reject)
            for (int i = 0; i < n; i++)
            {
                if (islands[i].IsWhitelisted) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (islands[j].IsWhitelisted) continue;
                    if (islands[i].SourceMesh != islands[j].SourceMesh) continue;
                    if (islands[i].UvChannel != islands[j].UvChannel) continue;

                    // AABB overlap check
                    if (BoundsOverlap(islands[i], islands[j]))
                    {
                        // Check actual UV vertex overlap (shared vertices)
                        var uvsA = new HashSet<long>(islands[i].UVs.Select(u => QuantizeUV(u)));
                        bool hasShared = islands[j].UVs.Any(u => uvsA.Contains(QuantizeUV(u)));
                        if (hasShared) Union(i, j);
                    }
                }
            }

            // Group and merge
            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(i);
                if (!groups.ContainsKey(root)) groups[root] = new List<int>();
                groups[root].Add(i);
            }

            var result = new List<UVIsland>();
            foreach (var group in groups.Values)
            {
                if (group.Count == 1)
                {
                    result.Add(islands[group[0]]);
                    continue;
                }

                // Merge into first island
                var merged = islands[group[0]];
                for (int i = 1; i < group.Count; i++)
                {
                    var other = islands[group[i]];
                    merged.TriangleIndices.AddRange(other.TriangleIndices);
                    merged.TrianglesUV.AddRange(other.TrianglesUV);

                    // Merge UVs (deduplicate)
                    var existingUVs = new HashSet<long>(merged.UVs.Select(u => QuantizeUV(u)));
                    foreach (var uv in other.UVs)
                    {
                        if (existingUVs.Add(QuantizeUV(uv)))
                            merged.UVs.Add(uv);
                    }

                    // Update bounds
                    merged.BoundsMin = Vector2.Min(merged.BoundsMin, other.BoundsMin);
                    merged.BoundsMax = Vector2.Max(merged.BoundsMax, other.BoundsMax);
                    merged.UVArea += other.UVArea;
                    merged.PhysicalArea += other.PhysicalArea;

                    // Whitelist if any is whitelisted
                    if (other.IsWhitelisted) merged.IsWhitelisted = true;
                }

                // Recalculate bounds
                var newBounds = TextureHelper.GetUVBounds(merged.UVs);
                merged.BoundsMin = newBounds.min;
                merged.BoundsMax = newBounds.max;

                result.Add(merged);
            }

            return result;
        }

        private static bool BoundsOverlap(UVIsland a, UVIsland b)
        {
            return a.BoundsMin.x <= b.BoundsMax.x && a.BoundsMax.x >= b.BoundsMin.x &&
                   a.BoundsMin.y <= b.BoundsMax.y && a.BoundsMax.y >= b.BoundsMin.y;
        }

        private static List<List<int>> GroupTrianglesIntoIslands(int[] triangles, List<Vector2> uvs)
        {
            int triCount = triangles.Length / 3;
            if (triCount == 0) return new List<List<int>>();

            int[] parent = new int[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;

            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

            var edgeMap = new Dictionary<long, int>();

            for (int t = 0; t < triCount; t++)
            {
                int i0 = triangles[t * 3], i1 = triangles[t * 3 + 1], i2 = triangles[t * 3 + 2];
                AddEdge(edgeMap, uvs, i0, i1, t, Union);
                AddEdge(edgeMap, uvs, i1, i2, t, Union);
                AddEdge(edgeMap, uvs, i2, i0, t, Union);
            }

            var groups = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                int root = Find(t);
                if (!groups.ContainsKey(root)) groups[root] = new List<int>();
                groups[root].Add(triangles[t * 3]);
                groups[root].Add(triangles[t * 3 + 1]);
                groups[root].Add(triangles[t * 3 + 2]);
            }
            return groups.Values.ToList();
        }

        private static void AddEdge(Dictionary<long, int> map, List<Vector2> uvs,
            int v0, int v1, int tri, System.Action<int, int> union)
        {
            if (v0 >= uvs.Count || v1 >= uvs.Count) return;
            long k0 = QuantizeUV(uvs[v0]), k1 = QuantizeUV(uvs[v1]);
            long key = k0 < k1 ? k0 * 100000007L + k1 : k1 * 100000007L + k0;
            if (map.TryGetValue(key, out int existing)) union(existing, tri);
            else map[key] = tri;
        }

        private static long QuantizeUV(Vector2 uv)
        {
            long x = (long)(uv.x * 65535.5f) & 0xFFFF;
            long y = (long)(uv.y * 65535.5f) & 0xFFFF;
            return (x << 16) | y;
        }

        private static float CalculatePhysicalArea(Mesh mesh, List<int> triIndices,
            Vector3[] vertices, Renderer renderer, ATOBuildContext atoCtx)
        {
            float area = 0;
            for (int i = 0; i + 2 < triIndices.Count; i += 3)
            {
                int a = triIndices[i], b = triIndices[i + 1], c = triIndices[i + 2];
                if (a < vertices.Length && b < vertices.Length && c < vertices.Length)
                    area += TextureHelper.CalculateTriangleArea(vertices[a], vertices[b], vertices[c]);
            }

            // Apply maximum animation scale (squared for area)
            float maxScale = 1f;
            if (atoCtx.AnimationAnalysis?.MaxScales != null)
            {
                var t = renderer.transform;
                while (t != null)
                {
                    if (atoCtx.AnimationAnalysis.MaxScales.TryGetValue(t, out float s))
                        maxScale = Mathf.Max(maxScale, s);
                    t = t.parent;
                }
            }
            area *= maxScale * maxScale;

            // World scale
            var ls = renderer.transform.lossyScale;
            float avgScale = (Mathf.Abs(ls.x) + Mathf.Abs(ls.y) + Mathf.Abs(ls.z)) / 3f;
            area *= avgScale * avgScale;

            return area;
        }

        private static void NormalizeUVBounds(UVIsland island, ATOBuildContext atoCtx)
        {
            var min = island.BoundsMin;
            var max = island.BoundsMax;
            var size = max - min;

            if (size.x <= 1f && size.y <= 1f)
            {
                Vector2 offset = Vector2.zero;
                if (min.x < 0 || max.x > 1) offset.x = -Mathf.Floor(min.x);
                if (min.y < 0 || max.y > 1) offset.y = -Mathf.Floor(min.y);

                if (offset != Vector2.zero)
                {
                    float nMinX = min.x + offset.x, nMaxX = max.x + offset.x;
                    float nMinY = min.y + offset.y, nMaxY = max.y + offset.y;
                    if (nMinX >= -0.001f && nMaxX <= 1.001f && nMinY >= -0.001f && nMaxY <= 1.001f)
                    {
                        for (int i = 0; i < island.UVs.Count; i++) island.UVs[i] += offset;
                        island.BoundsMin += offset;
                        island.BoundsMax += offset;
                        return;
                    }
                }
            }

            if (size.x > 1f || size.y > 1f)
            {
                island.IsWhitelisted = true;
                atoCtx.AddWarning($"UV island {island.Id} spans >1 UV tile. Whitelisted. / UV岛{island.Id}跨越>1个UV瓦片，已白名单。");
            }
        }

        private static void EvaluateBlendShapes(Mesh mesh, List<int> triIndices, UVIsland island)
        {
            if (mesh.blendShapeCount == 0) return;
            var vertices = mesh.vertices;
            var dv = new Vector3[mesh.vertexCount];
            var dn = new Vector3[mesh.vertexCount];
            var dt = new Vector3[mesh.vertexCount];

            for (int bs = 0; bs < mesh.blendShapeCount; bs++)
            {
                int frames = mesh.GetBlendShapeFrameCount(bs);
                mesh.GetBlendShapeFrameVertices(bs, frames - 1, dv, dn, dt);
                float modArea = 0;
                for (int i = 0; i + 2 < triIndices.Count; i += 3)
                {
                    int a = triIndices[i], b = triIndices[i + 1], c = triIndices[i + 2];
                    if (a < vertices.Length && b < vertices.Length && c < vertices.Length)
                        modArea += TextureHelper.CalculateTriangleArea(
                            vertices[a] + dv[a], vertices[b] + dv[b], vertices[c] + dv[c]);
                }
                island.PhysicalArea = Mathf.Max(island.PhysicalArea, modArea);
            }
        }

        private static void CheckPureColor(UVIsland island, ATOBuildContext atoCtx)
        {
            if (island.SourceTextureIndex < 0 || island.SourceTextureIndex >= atoCtx.AllTextures.Count) return;
            var texInfo = atoCtx.AllTextures[island.SourceTextureIndex];
            if (texInfo.Texture == null) return;

            // Get cached pixels
            Color[] pixels = null;
            int id = texInfo.InstanceId;
            if (!atoCtx.TexturePixelCache.TryGetValue(id, out pixels))
            {
                pixels = TextureHelper.ReadPixels(texInfo.Texture);
                if (pixels != null) atoCtx.TexturePixelCache[id] = pixels;
            }
            if (pixels == null) return;

            Color avgColor;
            island.IsPureColor = TextureHelper.IsRegionPureColor(pixels,
                texInfo.Width, texInfo.Height,
                Mathf.FloorToInt(island.BoundsMin.x * texInfo.Width),
                Mathf.FloorToInt(island.BoundsMin.y * texInfo.Height),
                Mathf.CeilToInt((island.BoundsMax.x - island.BoundsMin.x) * texInfo.Width),
                Mathf.CeilToInt((island.BoundsMax.y - island.BoundsMin.y) * texInfo.Height),
                out avgColor);
            island.PureColorValue = avgColor;
        }

        private static int FindSourceTextureIndex(Renderer renderer, int subMeshIdx,
            int uvChannel, ATOBuildContext atoCtx)
        {
            var materials = renderer.sharedMaterials;
            if (materials == null || subMeshIdx >= materials.Length) return -1;
            var mat = materials[subMeshIdx];
            if (mat == null) return -1;

            var mainTex = mat.GetTexture("_MainTex") as Texture2D;
            if (mainTex == null) mainTex = mat.GetTexture("_BaseMap") as Texture2D;

            for (int i = 0; i < atoCtx.AllTextures.Count; i++)
            {
                var ti = atoCtx.AllTextures[i];
                if (ti.Texture == mainTex || ti.OriginalTexture == mainTex) return i;
            }
            return -1;
        }
    }
}
