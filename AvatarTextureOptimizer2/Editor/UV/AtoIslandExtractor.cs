using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Connected-component UV islands; overlapping islands in the same texture are merged.
    /// UV 连通岛；同贴图重叠岛合并。
    /// </summary>
    public static class AtoIslandExtractor
    {
        public static List<AtoIsland> Extract(Mesh mesh, int submesh, int uvChannel, Texture2D tex)
        {
            var result = new List<AtoIsland>();
            if (mesh == null || tex == null) return result;
            var uv = AtoUvUtil.GetUv(mesh, uvChannel);
            if (uv == null || uv.Length == 0) return result;
            uv = AtoUvUtil.Normalize(uv, out _);

            var tris = mesh.GetTriangles(submesh);
            int triCount = tris.Length / 3;
            var adj = new List<int>[triCount];
            for (int t = 0; t < triCount; t++) adj[t] = new List<int>();

            var edge = new Dictionary<(int a, int b), int>();
            void AddEdge(int a, int b, int t)
            {
                if (a > b) (a, b) = (b, a);
                if (edge.TryGetValue((a, b), out var ot))
                {
                    adj[t].Add(ot);
                    adj[ot].Add(t);
                }
                else edge[(a, b)] = t;
            }

            for (int t = 0; t < triCount; t++)
            {
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                AddEdge(i0, i1, t);
                AddEdge(i1, i2, t);
                AddEdge(i2, i0, t);
            }

            var visited = new bool[triCount];
            for (int seed = 0; seed < triCount; seed++)
            {
                if (visited[seed]) continue;
                var stack = new Stack<int>();
                stack.Push(seed);
                visited[seed] = true;
                var islandTris = new List<int>();
                var verts = new HashSet<int>();
                while (stack.Count > 0)
                {
                    var t = stack.Pop();
                    islandTris.Add(t);
                    for (int k = 0; k < 3; k++) verts.Add(tris[t * 3 + k]);
                    foreach (var n in adj[t])
                        if (!visited[n]) { visited[n] = true; stack.Push(n); }
                }

                var isl = new AtoIsland
                {
                    Mesh = mesh,
                    UvChannel = uvChannel,
                    Submesh = submesh,
                    Source = tex,
                    Vertices = new List<int>(verts),
                    Triangles = islandTris
                };
                ComputeBounds(isl, uv, tex);
                result.Add(isl);
            }

            return MergeOverlapping(result);
        }

        static void ComputeBounds(AtoIsland isl, Vector2[] uv, Texture2D tex)
        {
            float minU = 1f, minV = 1f, maxU = 0f, maxV = 0f;
            foreach (var v in isl.Vertices)
            {
                var p = uv[v];
                minU = Mathf.Min(minU, p.x); minV = Mathf.Min(minV, p.y);
                maxU = Mathf.Max(maxU, p.x); maxV = Mathf.Max(maxV, p.y);
            }
            isl.UvBounds = Rect.MinMaxRect(minU, minV, maxU, maxV);
            isl.PixelBounds = Rect.MinMaxRect(
                minU * tex.width, minV * tex.height, maxU * tex.width, maxV * tex.height);
        }

        static List<AtoIsland> MergeOverlapping(List<AtoIsland> src)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < src.Count; i++)
                {
                    for (int j = i + 1; j < src.Count; j++)
                    {
                        if (!src[i].UvBounds.Overlaps(src[j].UvBounds, true)) continue;
                        src[i].Vertices.AddRange(src[j].Vertices);
                        src[i].Triangles.AddRange(src[j].Triangles);
                        var a = src[i].UvBounds;
                        var b = src[j].UvBounds;
                        src[i].UvBounds = Rect.MinMaxRect(
                            Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin),
                            Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
                        src.RemoveAt(j);
                        changed = true;
                        break;
                    }
                    if (changed) break;
                }
            }
            return src;
        }
    }
}
