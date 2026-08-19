using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Connected-component UV islands, wrap-seam detection, integer translate into [0,1], overlap merge.
    /// 连通域 UV 岛、跨缝检测、整体平移归一到 [0,1]、重叠岛合并。
    /// </summary>
    public sealed class UvIsland
    {
        public Mesh Mesh;
        public int Submesh;
        public int UvChannel;
        public readonly List<int> Triangles = new List<int>(); // index into mesh.triangles (groups of 3 vertex indices)
        public readonly List<int> VertexIndices = new List<int>();
        public Vector2 MinUv;
        public Vector2 MaxUv;
        public Vector2 MinUvNorm;
        public Vector2 MaxUvNorm;
        public Vector2 Translate; // subtracted from raw UV to normalize
        public bool CrossesSeam;
        public bool Normalized;
        public float WorldArea;
        public int OrigPixelW;
        public int OrigPixelH;
        public int ScaledW;
        public int ScaledH;
        public float ScaleU = 1f;
        public float ScaleV = 1f;
        public bool SolidColor;
        public Texture2D SourceTexture;
        public int IslandId;
        /// <summary>Cached 4px raster of the scaled island. Disposed by the session. / 缩放后岛的 4px 光栅缓存，由 session 释放。</summary>
        public BitmaskRaster.Mask? CachedMask;

        public float UvWidth => Mathf.Max(1e-8f, MaxUvNorm.x - MinUvNorm.x);
        public float UvHeight => Mathf.Max(1e-8f, MaxUvNorm.y - MinUvNorm.y);

        public Vector2 Normalize(Vector2 uv) => uv - Translate;
    }

    public static class UvIslandExtractor
    {
        public static List<UvIsland> Extract(Mesh mesh, int submesh, int uvChannel, AtoLog log)
        {
            var result = new List<UvIsland>();
            if (mesh == null || submesh < 0 || submesh >= mesh.subMeshCount) return result;
            if (uvChannel < 0 || uvChannel > 7) return result;

            var uvs = new List<Vector2>();
            mesh.GetUVs(uvChannel, uvs);
            if (uvs == null || uvs.Count == 0)
            {
                log?.VerboseInfo(mesh.name + " has no UV" + uvChannel);
                return result;
            }

            var tris = mesh.GetTriangles(submesh);
            if (tris == null || tris.Length < 3) return result;

            var nTri = tris.Length / 3;
            var parent = new int[nTri];
            for (int i = 0; i < nTri; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }

                return x;
            }

            void Union(int a, int b)
            {
                a = Find(a);
                b = Find(b);
                if (a != b) parent[b] = a;
            }

            // Edge key in UV space (quantized) → triangle list. / UV 空间量化边 → 三角形。
            var edges = new Dictionary<long, int>(nTri * 2);
            for (int t = 0; t < nTri; t++)
            {
                var i0 = tris[t * 3];
                var i1 = tris[t * 3 + 1];
                var i2 = tris[t * 3 + 2];
                if (i0 >= uvs.Count || i1 >= uvs.Count || i2 >= uvs.Count) continue;
                Link(t, i0, i1);
                Link(t, i1, i2);
                Link(t, i2, i0);
            }

            void Link(int tri, int a, int b)
            {
                var key = EdgeKey(uvs[a], uvs[b]);
                if (edges.TryGetValue(key, out var other) && other != tri) Union(tri, other);
                else edges[key] = tri;
            }

            var groups = new Dictionary<int, UvIsland>();
            for (int t = 0; t < nTri; t++)
            {
                var root = Find(t);
                if (!groups.TryGetValue(root, out var island))
                {
                    island = new UvIsland
                    {
                        Mesh = mesh,
                        Submesh = submesh,
                        UvChannel = uvChannel,
                        MinUv = new Vector2(float.PositiveInfinity, float.PositiveInfinity),
                        MaxUv = new Vector2(float.NegativeInfinity, float.NegativeInfinity)
                    };
                    groups[root] = island;
                    result.Add(island);
                }

                var i0 = tris[t * 3];
                var i1 = tris[t * 3 + 1];
                var i2 = tris[t * 3 + 2];
                island.Triangles.Add(i0);
                island.Triangles.Add(i1);
                island.Triangles.Add(i2);
                Expand(island, uvs, i0);
                Expand(island, uvs, i1);
                Expand(island, uvs, i2);
            }

            foreach (var island in result)
            {
                FinalizeNormalize(island, uvs, log);
                var set = new HashSet<int>();
                foreach (var v in island.Triangles) set.Add(v);
                island.VertexIndices.AddRange(set);
            }

            return result;
        }

        static void Expand(UvIsland island, List<Vector2> uvs, int idx)
        {
            if (idx < 0 || idx >= uvs.Count) return;
            var uv = uvs[idx];
            island.MinUv = Vector2.Min(island.MinUv, uv);
            island.MaxUv = Vector2.Max(island.MaxUv, uv);
        }

        static void FinalizeNormalize(UvIsland island, List<Vector2> uvs, AtoLog log)
        {
            var size = island.MaxUv - island.MinUv;
            // Crosses a wrap seam if the island itself spans more than 1 + epsilon in a axis.
            // 岛自身跨度超过 1 则认为跨缝。
            if (size.x > 1.0001f || size.y > 1.0001f)
            {
                island.CrossesSeam = true;
                island.MinUvNorm = island.MinUv;
                island.MaxUvNorm = island.MaxUv;
                return;
            }

            // Integer translate so the bbox sits in [0,1] if possible.
            // 若能通过整数平移把包围盒放进 [0,1] 则归一。
            var tx = Mathf.Floor(island.MinUv.x);
            var ty = Mathf.Floor(island.MinUv.y);
            var min = island.MinUv - new Vector2(tx, ty);
            var max = island.MaxUv - new Vector2(tx, ty);
            if (min.x >= -1e-4f && min.y >= -1e-4f && max.x <= 1.0001f && max.y <= 1.0001f)
            {
                island.Translate = new Vector2(tx, ty);
                island.MinUvNorm = Vector2.Max(min, Vector2.zero);
                island.MaxUvNorm = Vector2.Min(max, Vector2.one);
                island.Normalized = true;
                return;
            }

            // Cannot place into [0,1] without wrapping. / 无法不跨缝地放进 [0,1]。
            island.CrossesSeam = true;
            island.MinUvNorm = island.MinUv;
            island.MaxUvNorm = island.MaxUv;
        }

        /// <summary>
        /// Merge islands of the same texture whose UV bboxes overlap. / 合并同贴图中 UV 包围盒重叠的岛。
        /// </summary>
        public static List<UvIsland> MergeOverlapping(List<UvIsland> islands)
        {
            if (islands == null || islands.Count <= 1) return islands;
            var list = new List<UvIsland>(islands);
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (list[i].Mesh != list[j].Mesh) continue;
                        if (list[i].UvChannel != list[j].UvChannel) continue;
                        if (list[i].SourceTexture != list[j].SourceTexture) continue;
                        if (!Overlaps(list[i], list[j])) continue;
                        MergeInto(list[i], list[j]);
                        list.RemoveAt(j);
                        changed = true;
                        break;
                    }

                    if (changed) break;
                }
            }

            return list;
        }

        static bool Overlaps(UvIsland a, UvIsland b)
        {
            return a.MinUvNorm.x <= b.MaxUvNorm.x && a.MaxUvNorm.x >= b.MinUvNorm.x &&
                   a.MinUvNorm.y <= b.MaxUvNorm.y && a.MaxUvNorm.y >= b.MinUvNorm.y;
        }

        static void MergeInto(UvIsland a, UvIsland b)
        {
            a.Triangles.AddRange(b.Triangles);
            foreach (var v in b.VertexIndices)
                if (!a.VertexIndices.Contains(v))
                    a.VertexIndices.Add(v);
            a.MinUv = Vector2.Min(a.MinUv, b.MinUv);
            a.MaxUv = Vector2.Max(a.MaxUv, b.MaxUv);
            a.MinUvNorm = Vector2.Min(a.MinUvNorm, b.MinUvNorm);
            a.MaxUvNorm = Vector2.Max(a.MaxUvNorm, b.MaxUvNorm);
            a.CrossesSeam = a.CrossesSeam || b.CrossesSeam;
            a.WorldArea += b.WorldArea;
        }

        static long EdgeKey(Vector2 a, Vector2 b)
        {
            const float q = 4096f;
            int ax = (int)math.round(a.x * q);
            int ay = (int)math.round(a.y * q);
            int bx = (int)math.round(b.x * q);
            int by = (int)math.round(b.y * q);
            long ka = ((long)(uint)ax << 32) ^ (uint)ay;
            long kb = ((long)(uint)bx << 32) ^ (uint)by;
            return ka < kb ? (ka * 397) ^ kb : (kb * 397) ^ ka;
        }
    }
}
