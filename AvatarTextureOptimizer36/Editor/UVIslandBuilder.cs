using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Fosa.AvatarTextureOptimizer;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Builds connected and overlapping UV islands from actual mesh triangles. / 根据真实网格三角形建立连通且重叠合并的 UV 岛。
    /// </summary>
    internal static class UVIslandBuilder
    {
        public static void Build(BuildSnapshot snapshot, AvatarTextureOptimizer component, ATOLogger logger, ATOProgress progress)
        {
            for (int rendererIndex = 0; rendererIndex < snapshot.Renderers.Count; rendererIndex++)
            {
                RendererRecord renderer = snapshot.Renderers[rendererIndex];
                if (renderer.SkipAll) continue;
                for (int materialIndex = 0; materialIndex < renderer.Materials.Count; materialIndex++)
                {
                    MaterialUse use = renderer.Materials[materialIndex];
                    if (use.SkipAll || use.MainReference == null) continue;
                    HashSet<int> channels = new HashSet<int>();
                    for (int referenceIndex = 0; referenceIndex < use.References.Count; referenceIndex++)
                        channels.Add(use.References[referenceIndex].UVChannel);

                    foreach (int channel in channels)
                    {
                        List<IslandRecord> built = BuildForChannel(renderer, use, channel, component, snapshot, logger);
                        for (int i = 0; i < built.Count; i++)
                        {
                            use.Islands.Add(built[i]);
                            snapshot.Islands.Add(built[i]);
                        }
                    }
                }
                progress.Step(0.05f + 0.90f * ((rendererIndex + 1) / (float)Math.Max(1, snapshot.Renderers.Count)),
                    "Build islands " + (rendererIndex + 1) + "/" + snapshot.Renderers.Count + " / 建立 UV 岛");
            }
            logger.Info("Built " + snapshot.Islands.Count + " UV island(s). / 建立 UV 岛数量：" + snapshot.Islands.Count);
        }

        private static List<IslandRecord> BuildForChannel(RendererRecord renderer, MaterialUse use, int channel,
            AvatarTextureOptimizer component, BuildSnapshot snapshot, ATOLogger logger)
        {
            Mesh mesh = renderer.SourceMesh;
            List<Vector4> uv4 = new List<Vector4>();
            mesh.GetUVs(channel, uv4);
            if (uv4.Count != mesh.vertexCount)
            {
                if (channel == 0)
                {
                    Vector2[] uv = mesh.uv;
                    if (uv != null && uv.Length == mesh.vertexCount)
                    {
                        uv4.Clear();
                        for (int i = 0; i < uv.Length; i++) uv4.Add(new Vector4(uv[i].x, uv[i].y, 0f, 0f));
                    }
                }
            }
            if (uv4.Count != mesh.vertexCount)
            {
                logger.Warning("Mesh '" + mesh.name + "' has no complete UV channel " + channel + "; that UV use is skipped. / 网格缺少完整 UV 通道，已跳过。");
                renderer.UnsafeUVChannels.Add(channel);
                return new List<IslandRecord>();
            }

            int[] triangles;
            try
            {
                triangles = mesh.GetTriangles(use.Slot, true);
            }
            catch (Exception exception)
            {
                logger.Warning("Could not read submesh " + use.Slot + " from '" + mesh.name + "'; skipped. / 无法读取子网格，已跳过。 " + exception.Message);
                renderer.UnsafeUVChannels.Add(channel);
                return new List<IslandRecord>();
            }
            if (triangles == null || triangles.Length < 3) return new List<IslandRecord>();

            int triangleCount = triangles.Length / 3;
            DisjointSet set = new DisjointSet(triangleCount);
            Dictionary<int, int> firstTriangleByVertex = new Dictionary<int, int>();
            for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
            {
                for (int corner = 0; corner < 3; corner++)
                {
                    int vertex = triangles[triangleIndex * 3 + corner];
                    int first;
                    if (firstTriangleByVertex.TryGetValue(vertex, out first)) set.Union(triangleIndex, first);
                    else firstTriangleByVertex.Add(vertex, triangleIndex);
                }
            }

            Dictionary<int, List<int>> groups = new Dictionary<int, List<int>>();
            for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
            {
                int root = set.Find(triangleIndex);
                List<int> group;
                if (!groups.TryGetValue(root, out group))
                {
                    group = new List<int>();
                    groups.Add(root, group);
                }
                group.Add(triangleIndex);
            }

            Vector3[] vertices = mesh.vertices;
            List<IslandRecord> islands = new List<IslandRecord>();
            foreach (List<int> group in groups.Values)
            {
                IslandRecord island = new IslandRecord
                {
                    Material = use,
                    SubMesh = use.Slot,
                    UVChannel = channel,
                    TypeGroupKey = string.Join("|", use.References.Where(r => r.UVChannel == channel)
                        .Select(r => r.TypeGroupKey).Distinct().OrderBy(k => k).ToArray()),
                    UVBounds = new Rect(float.PositiveInfinity, float.PositiveInfinity, 0f, 0f),
                    SkipAtlas = use.SkipAtlas
                };
                Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
                float uvArea = 0f;
                float surfaceArea = 0f;
                for (int groupIndex = 0; groupIndex < group.Count; groupIndex++)
                {
                    int triangleIndex = group[groupIndex];
                    int a = triangles[triangleIndex * 3];
                    int b = triangles[triangleIndex * 3 + 1];
                    int c = triangles[triangleIndex * 3 + 2];
                    Vector2 uvA = new Vector2(uv4[a].x, uv4[a].y);
                    Vector2 uvB = new Vector2(uv4[b].x, uv4[b].y);
                    Vector2 uvC = new Vector2(uv4[c].x, uv4[c].y);
                    float triangleUVArea = Mathf.Abs(Cross(uvB - uvA, uvC - uvA)) * 0.5f;
                    if (triangleUVArea <= 1e-10f) continue;
                    island.Triangles.Add(new IslandTriangle(a, b, c, uvA, uvB, uvC, triangleUVArea));
                    uvArea += triangleUVArea;
                    min = Vector2.Min(min, Vector2.Min(uvA, Vector2.Min(uvB, uvC)));
                    max = Vector2.Max(max, Vector2.Max(uvA, Vector2.Max(uvB, uvC)));
                    if (vertices != null && vertices.Length == mesh.vertexCount)
                        surfaceArea += TriangleArea(vertices[a], vertices[b], vertices[c]);
                }
                if (island.Triangles.Count == 0) continue;
                island.OriginalUVArea = uvArea;
                island.SurfaceArea = EstimateMaxSurfaceArea(mesh, island.Triangles, surfaceArea) * renderer.AnimationAreaScale;
                island.UVBounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);

                Vector2 translation;
                if (!TryNormalizeUV(island, use, component, out translation, logger))
                {
                    island.SkipAtlas = true;
                    renderer.UnsafeUVChannels.Add(channel);
                }
                else
                {
                    island.UVTranslation = translation;
                    island.NormalizedByTranslation = translation != Vector2.zero;
                    island.UVBounds = GetTranslatedBounds(island, translation);
                }

                TextureAssetInfo texture = island.PrimaryTexture;
                if (texture != null)
                {
                    island.PureColor = TextureContentClassifier.IsPureRegion(snapshot.PixelCache, texture.Source, island.UVBounds, logger);
                }
                islands.Add(island);
            }

            MergeOverlappingIslands(islands, logger);
            return islands;
        }

        private static bool TryNormalizeUV(IslandRecord island, MaterialUse use, AvatarTextureOptimizer component,
            out Vector2 translation, ATOLogger logger)
        {
            translation = Vector2.zero;
            Rect bounds = island.UVBounds;
            bool outside = bounds.xMin < -0.00001f || bounds.yMin < -0.00001f || bounds.xMax > 1.00001f || bounds.yMax > 1.00001f;
            if (!outside) return true;
            if (!component.allowUVTranslationIntoUnitSquare) return false;

            int tileX = Mathf.FloorToInt(bounds.xMin + 0.00001f);
            int tileY = Mathf.FloorToInt(bounds.yMin + 0.00001f);
            if (Mathf.Abs(bounds.xMax - bounds.xMin) > 1.00001f || Mathf.Abs(bounds.yMax - bounds.yMin) > 1.00001f)
                return false;
            if (Mathf.FloorToInt(bounds.xMax - 0.00001f) != tileX || Mathf.FloorToInt(bounds.yMax - 0.00001f) != tileY)
                return false;

            for (int i = 0; i < use.References.Count; i++)
            {
                TextureReference reference = use.References[i];
                if (reference.UVChannel != island.UVChannel || reference.Texture == null) continue;
                if (reference.Texture.WrapMode != TextureWrapMode.Repeat) return false;
            }
            translation = new Vector2(-tileX, -tileY);
            logger.Detail("Translated UV island by " + translation + " to [0,1]. / UV 岛整体平移归一化。");
            return true;
        }

        private static Rect GetTranslatedBounds(IslandRecord island, Vector2 translation)
        {
            Rect bounds = island.UVBounds;
            return new Rect(bounds.x + translation.x, bounds.y + translation.y, bounds.width, bounds.height);
        }

        private static void MergeOverlappingIslands(List<IslandRecord> islands, ATOLogger logger)
        {
            bool merged;
            do
            {
                merged = false;
                for (int i = 0; i < islands.Count && !merged; i++)
                {
                    for (int j = i + 1; j < islands.Count; j++)
                    {
                        if (!islands[i].UVBounds.Overlaps(islands[j].UVBounds, true)) continue;
                        if ((islands[i].UVTranslation - islands[j].UVTranslation).sqrMagnitude > 1e-8f) continue;
                        IslandRecord first = islands[i];
                        IslandRecord second = islands[j];
                        first.Triangles.AddRange(second.Triangles);
                        first.UVBounds = Rect.MinMaxRect(
                            Mathf.Min(first.UVBounds.xMin, second.UVBounds.xMin),
                            Mathf.Min(first.UVBounds.yMin, second.UVBounds.yMin),
                            Mathf.Max(first.UVBounds.xMax, second.UVBounds.xMax),
                            Mathf.Max(first.UVBounds.yMax, second.UVBounds.yMax));
                        first.OriginalUVArea += second.OriginalUVArea;
                        first.SurfaceArea += second.SurfaceArea;
                        first.SkipAtlas |= second.SkipAtlas;
                        islands.RemoveAt(j);
                        merged = true;
                        logger.Detail("Merged overlapping UV islands. / 合并重叠 UV 岛。");
                        break;
                    }
                }
            } while (merged);
        }

        private static float EstimateMaxSurfaceArea(Mesh mesh, List<IslandTriangle> triangles, float baseArea)
        {
            float maximum = baseArea;
            Vector3[] deltaVertices = new Vector3[mesh.vertexCount];
            Vector3[] deltaNormals = new Vector3[mesh.vertexCount];
            Vector4[] deltaTangents = new Vector4[mesh.vertexCount];
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                int frameCount = mesh.GetBlendShapeFrameCount(shape);
                if (frameCount <= 0) continue;
                float weight = mesh.GetBlendShapeFrameWeight(shape, frameCount - 1);
                if (Mathf.Abs(weight - 100f) > 0.001f) continue;
                mesh.GetBlendShapeFrameVertices(shape, frameCount - 1, deltaVertices, deltaNormals, deltaTangents);
                float area = 0f;
                for (int i = 0; i < triangles.Count; i++)
                {
                    IslandTriangle triangle = triangles[i];
                    Vector3[] baseVertices = mesh.vertices;
                    area += TriangleArea(baseVertices[triangle.A] + deltaVertices[triangle.A],
                        baseVertices[triangle.B] + deltaVertices[triangle.B],
                        baseVertices[triangle.C] + deltaVertices[triangle.C]);
                }
                if (area > maximum) maximum = area;
            }
            return maximum;
        }

        private static float TriangleArea(Vector3 a, Vector3 b, Vector3 c)
        {
            return Vector3.Cross(b - a, c - a).magnitude * 0.5f;
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private sealed class DisjointSet
        {
            private readonly int[] _parent;
            private readonly byte[] _rank;

            public DisjointSet(int count)
            {
                _parent = new int[count];
                _rank = new byte[count];
                for (int i = 0; i < count; i++) _parent[i] = i;
            }

            public int Find(int value)
            {
                while (_parent[value] != value)
                {
                    _parent[value] = _parent[_parent[value]];
                    value = _parent[value];
                }
                return value;
            }

            public void Union(int left, int right)
            {
                int a = Find(left);
                int b = Find(right);
                if (a == b) return;
                if (_rank[a] < _rank[b]) _parent[a] = b;
                else if (_rank[a] > _rank[b]) _parent[b] = a;
                else
                {
                    _parent[b] = a;
                    _rank[a]++;
                }
            }
        }
    }

    internal static class TextureContentClassifier
    {
        public static bool IsPureRegion(TexturePixelCache cache, Texture2D texture, Rect uvBounds, ATOLogger logger)
        {
            if (texture == null || cache == null) return false;
            TexturePixelData data = cache.Get(texture, logger);
            if (data == null) return false;
            int minX = Mathf.Clamp(Mathf.FloorToInt(uvBounds.xMin * data.Width), 0, data.Width - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(uvBounds.xMax * data.Width), minX + 1, data.Width);
            int minY = Mathf.Clamp(Mathf.FloorToInt(uvBounds.yMin * data.Height), 0, data.Height - 1);
            int maxY = Mathf.Clamp(Mathf.CeilToInt(uvBounds.yMax * data.Height), minY + 1, data.Height);
            Color32 first = data.Get(minX, minY);
            int stride = Math.Max(1, Math.Max(maxX - minX, maxY - minY) / 16);
            for (int y = minY; y < maxY; y += stride)
            {
                for (int x = minX; x < maxX; x += stride)
                {
                    Color32 current = data.Get(x, y);
                    if (Mathf.Abs(current.r - first.r) > 1 || Mathf.Abs(current.g - first.g) > 1 ||
                        Mathf.Abs(current.b - first.b) > 1 || Mathf.Abs(current.a - first.a) > 1) return false;
                }
            }
            return true;
        }
    }
}
