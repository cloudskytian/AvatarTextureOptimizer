// English: Extract UV islands, normalize out-of-range tiles, merge overlaps, evaluate world area.
// 中文：提取 UV 岛、归一越界整块、合并重叠岛、计算世界面积（形态键 0/100 + 最大缩放）。
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.Rendering;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOIslandExtractor
    {
        private const float UvEps = 1e-5f;

        public static void Extract(ATOState state)
        {
            var nextId = 0;
            foreach (var use in state.Uses)
            {
                if (!use.Eligible || use.Texture == null) continue;
                if (use.Renderer == null || use.Renderer.Mesh == null) continue;
                var mesh = use.Renderer.Mesh;
                if (use.UvChannel < 0 || use.UvChannel >= 8) continue;
                if (mesh.subMeshCount <= 0) continue;

                var uvs = new List<Vector2>();
                mesh.GetUVs(use.UvChannel, uvs);
                if (uvs == null || uvs.Count < 3)
                {
                    state.Log.VerboseInfo("no UV" + use.UvChannel + " on " + mesh.name);
                    continue;
                }

                var slot = FindSlot(use);
                var sub = Mathf.Clamp(slot, 0, mesh.subMeshCount - 1);
                int[] tris;
                try { tris = mesh.GetTriangles(sub, true); }
                catch { continue; }
                if (tris == null || tris.Length < 3) continue;

                Vector2 translate;
                if (!TryNormalize(uvs, tris, out translate))
                {
                    use.Eligible = false;
                    use.SkipReason = "UV wrap-crossing";
                    state.Report.Warnings.Add((use.Texture.name) + " UV wrap-crossing");
                    ErrorReport.ReportError(ATOLoc.L, ErrorSeverity.NonFatal, "warn.uvWrap", use.Texture.name);
                    continue;
                }

                if (translate.sqrMagnitude > 0f)
                {
                    for (var ui = 0; ui < uvs.Count; ui++) uvs[ui] = uvs[ui] + translate;
                }

                var islands = BuildIslands(tris, uvs);
                var world = EvaluateWorld(use.Renderer, mesh);
                var scaleMul = MaxScaleFactor(use.Renderer.MaxAbsScale);
                var tw = Mathf.Max(1, use.Texture.width);
                var th = Mathf.Max(1, use.Texture.height);

                foreach (var isl in islands)
                {
                    isl.Id = nextId++;
                    isl.Renderer = use.Renderer;
                    isl.Submesh = sub;
                    isl.UvChannel = use.UvChannel;
                    isl.Source = use.Texture;
                    isl.Semantic = use.Semantic;
                    isl.UvTranslate = translate;
                    isl.UvBounds = BoundsOf(isl, uvs);
                    isl.PixelBounds = new Rect(
                        isl.UvBounds.xMin * tw,
                        isl.UvBounds.yMin * th,
                        Mathf.Max(1f, isl.UvBounds.width * tw),
                        Mathf.Max(1f, isl.UvBounds.height * th));
                    isl.UvArea = Mathf.Max(1e-12f, AreaUv(isl, uvs));
                    isl.WorldArea = Mathf.Max(1e-12f, AreaWorld(isl, tris, world) * scaleMul * scaleMul);
                    DetectSolid(state, isl);
                    state.Islands.Add(isl);
                }
            }

            MergeOverlaps(state);
            state.Report.IslandsExtracted = state.Islands.Count;
            state.Log.Info("islands extracted=" + state.Islands.Count);
        }

        private static int FindSlot(ATOTextureUse use)
        {
            if (use.Renderer == null || use.Renderer.Materials == null || use.Material == null) return 0;
            for (var i = 0; i < use.Renderer.Materials.Length; i++)
            {
                if (use.Renderer.Materials[i] == use.Material) return i;
            }

            return 0;
        }

        /// <summary>
        /// If every used UV lies in a single 1x1 tile, translate that tile onto [0,1]. Crossing tiles => fail.
        /// 若全部已用 UV 落在同一 1x1 瓦片内，则平移到 [0,1]；跨瓦片则失败。
        /// </summary>
        internal static bool TryNormalize(List<Vector2> uvs, int[] tris, out Vector2 translate)
        {
            translate = Vector2.zero;
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (var i = 0; i < tris.Length; i++)
            {
                var vi = tris[i];
                if (vi < 0 || vi >= uvs.Count) continue;
                var uv = uvs[vi];
                min = Vector2.Min(min, uv);
                max = Vector2.Max(max, uv);
            }

            if (float.IsInfinity(min.x)) return true;
            var tileX = Mathf.Floor(min.x);
            var tileY = Mathf.Floor(min.y);
            // Crosses a wrap seam if the bbox spans more than one integer tile.
            if (max.x - min.x > 1f + 1e-4f || max.y - min.y > 1f + 1e-4f) return false;
            if (Mathf.Floor(max.x - 1e-5f) > tileX && max.x - min.x > 1e-4f) return false;
            if (Mathf.Floor(max.y - 1e-5f) > tileY && max.y - min.y > 1e-4f) return false;
            translate = new Vector2(-tileX, -tileY);
            if (translate.sqrMagnitude > 0)
            {
                for (var i = 0; i < uvs.Count; i++)
                    uvs[i] = uvs[i] + translate;
            }

            return true;
        }

        private static List<ATOIsland> BuildIslands(int[] tris, List<Vector2> uvs)
        {
            var triCount = tris.Length / 3;
            var parent = new int[triCount];
            for (var i = 0; i < triCount; i++) parent[i] = i;

            var edgeMap = new Dictionary<long, int>();
            for (var t = 0; t < triCount; t++)
            {
                var a = tris[t * 3];
                var b = tris[t * 3 + 1];
                var c = tris[t * 3 + 2];
                ConnectEdge(edgeMap, parent, t, a, b, uvs);
                ConnectEdge(edgeMap, parent, t, b, c, uvs);
                ConnectEdge(edgeMap, parent, t, c, a, uvs);
            }

            var buckets = new Dictionary<int, ATOIsland>();
            for (var t = 0; t < triCount; t++)
            {
                var r = Find(parent, t);
                ATOIsland isl;
                if (!buckets.TryGetValue(r, out isl))
                {
                    isl = new ATOIsland();
                    buckets[r] = isl;
                }

                isl.TriangleIndices.Add(t);
                isl.VertexIndices.Add(tris[t * 3]);
                isl.VertexIndices.Add(tris[t * 3 + 1]);
                isl.VertexIndices.Add(tris[t * 3 + 2]);
            }

            return new List<ATOIsland>(buckets.Values);
        }

        private static void ConnectEdge(Dictionary<long, int> edgeMap, int[] parent, int tri, int i0, int i1,
            List<Vector2> uvs)
        {
            if (i0 < 0 || i1 < 0 || i0 >= uvs.Count || i1 >= uvs.Count) return;
            var k0 = Quant(uvs[i0]);
            var k1 = Quant(uvs[i1]);
            var key = k0 <= k1 ? (k0 << 32) ^ k1 : (k1 << 32) ^ k0;
            int other;
            if (edgeMap.TryGetValue(key, out other))
            {
                Union(parent, tri, other);
            }
            else
            {
                edgeMap[key] = tri;
            }
        }

        private static long Quant(Vector2 uv)
        {
            var x = (long)Mathf.Round(uv.x * 1048576f);
            var y = (long)Mathf.Round(uv.y * 1048576f);
            return ((x & 0xffffffffL) << 32) ^ (y & 0xffffffffL);
        }

        private static int Find(int[] p, int i)
        {
            while (p[i] != i)
            {
                p[i] = p[p[i]];
                i = p[i];
            }

            return i;
        }

        private static void Union(int[] p, int a, int b)
        {
            a = Find(p, a);
            b = Find(p, b);
            if (a != b) p[b] = a;
        }

        private static Rect BoundsOf(ATOIsland isl, List<Vector2> uvs)
        {
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (var vi in isl.VertexIndices)
            {
                if (vi < 0 || vi >= uvs.Count) continue;
                min = Vector2.Min(min, uvs[vi]);
                max = Vector2.Max(max, uvs[vi]);
            }

            if (float.IsInfinity(min.x)) return new Rect(0, 0, 0, 0);
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private static float AreaUv(ATOIsland isl, List<Vector2> uvs)
        {
            // Use pixel-space later; UV area of triangles.
            return Mathf.Max(1e-8f, isl.UvBounds.width * isl.UvBounds.height);
        }

        private static Vector3[] EvaluateWorld(ATORendererInfo info, Mesh mesh)
        {
            var verts = mesh.vertices;
            if (verts == null) return Array.Empty<Vector3>();
            var best = (Vector3[])verts.Clone();
            var l2w = info.Renderer != null ? info.Renderer.localToWorldMatrix : Matrix4x4.identity;

            var shapes = mesh.blendShapeCount;
            if (shapes > 0)
            {
                var delta = new Vector3[verts.Length];
                var nrm = new Vector3[verts.Length];
                var tan = new Vector3[verts.Length];
                for (var s = 0; s < shapes; s++)
                {
                    var frames = mesh.GetBlendShapeFrameCount(s);
                    if (frames <= 0) continue;
                    // Only weight 0 (basis) and the last frame (treated as 100).
                    mesh.GetBlendShapeFrameVertices(s, frames - 1, delta, nrm, tan);
                    for (var i = 0; i < verts.Length; i++)
                    {
                        var v = verts[i] + delta[i];
                        // Keep the vertex that is farther from the mesh centroid approximation: max area uses both.
                        // Store max displacement per-axis magnitude into best as a conservative envelope.
                        best[i] = MaxAbs(best[i], v);
                    }
                }
            }

            for (var i = 0; i < best.Length; i++) best[i] = l2w.MultiplyPoint3x4(best[i]);
            return best;
        }

        private static Vector3 MaxAbs(Vector3 a, Vector3 b)
        {
            return new Vector3(
                Mathf.Abs(a.x) >= Mathf.Abs(b.x) ? a.x : b.x,
                Mathf.Abs(a.y) >= Mathf.Abs(b.y) ? a.y : b.y,
                Mathf.Abs(a.z) >= Mathf.Abs(b.z) ? a.z : b.z);
        }

        private static float AreaWorld(ATOIsland isl, int[] tris, Vector3[] world)
        {
            var sum = 0f;
            foreach (var t in isl.TriangleIndices)
            {
                var i0 = tris[t * 3];
                var i1 = tris[t * 3 + 1];
                var i2 = tris[t * 3 + 2];
                if (i0 >= world.Length || i1 >= world.Length || i2 >= world.Length) continue;
                sum += Vector3.Cross(world[i1] - world[i0], world[i2] - world[i0]).magnitude * 0.5f;
            }

            return sum;
        }

        private static float MaxScaleFactor(Vector3 s)
        {
            return Mathf.Max(1e-4f, Mathf.Max(s.x, Mathf.Max(s.y, s.z)));
        }

        private static void DetectSolid(ATOState state, ATOIsland isl)
        {
            var dec = state.Cache.Get(isl.Source, state.Log);
            if (dec == null) return;
            var x0 = Mathf.Clamp(Mathf.FloorToInt(isl.PixelBounds.xMin), 0, dec.Width - 1);
            var y0 = Mathf.Clamp(Mathf.FloorToInt(isl.PixelBounds.yMin), 0, dec.Height - 1);
            var x1 = Mathf.Clamp(Mathf.CeilToInt(isl.PixelBounds.xMax), 0, dec.Width);
            var y1 = Mathf.Clamp(Mathf.CeilToInt(isl.PixelBounds.yMax), 0, dec.Height);
            if (x1 <= x0 || y1 <= y0) return;
            var first = dec.Get(x0, y0);
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var c = dec.Get(x, y);
                    if (Mathf.Abs(c.r - first.r) > 2 || Mathf.Abs(c.g - first.g) > 2 ||
                        Mathf.Abs(c.b - first.b) > 2 || Mathf.Abs(c.a - first.a) > 2)
                    {
                        isl.SolidColor = false;
                        return;
                    }
                }
            }

            isl.SolidColor = true;
            isl.Solid = first;
        }

        private static void MergeOverlaps(ATOState state)
        {
            // Same source texture + overlapping pixel bounds => merge (user requirement).
            var byTex = new Dictionary<Texture2D, List<ATOIsland>>();
            foreach (var isl in state.Islands)
            {
                if (isl.Source == null) continue;
                List<ATOIsland> list;
                if (!byTex.TryGetValue(isl.Source, out list))
                {
                    list = new List<ATOIsland>();
                    byTex[isl.Source] = list;
                }

                list.Add(isl);
            }

            var remove = new HashSet<ATOIsland>();
            foreach (var kv in byTex)
            {
                var list = kv.Value;
                for (var i = 0; i < list.Count; i++)
                {
                    if (remove.Contains(list[i])) continue;
                    for (var j = i + 1; j < list.Count; j++)
                    {
                        if (remove.Contains(list[j])) continue;
                        if (!list[i].PixelBounds.Overlaps(list[j].PixelBounds, true)) continue;
                        MergeInto(list[i], list[j]);
                        remove.Add(list[j]);
                    }
                }
            }

            if (remove.Count == 0) return;
            state.Islands.RemoveAll(remove.Contains);
            state.Log.Info("merged overlapping islands removed=" + remove.Count + " remain=" + state.Islands.Count);
        }

        private static void MergeInto(ATOIsland a, ATOIsland b)
        {
            a.VertexIndices.AddRange(b.VertexIndices);
            a.TriangleIndices.AddRange(b.TriangleIndices);
            a.UvBounds = Union(a.UvBounds, b.UvBounds);
            a.PixelBounds = Union(a.PixelBounds, b.PixelBounds);
            a.WorldArea += b.WorldArea;
            a.UvArea += b.UvArea;
            a.SolidColor = a.SolidColor && b.SolidColor && a.Solid == b.Solid;
        }

        private static Rect Union(Rect a, Rect b)
        {
            var x0 = Mathf.Min(a.xMin, b.xMin);
            var y0 = Mathf.Min(a.yMin, b.yMin);
            var x1 = Mathf.Max(a.xMax, b.xMax);
            var y1 = Mathf.Max(a.yMax, b.yMax);
            return Rect.MinMaxRect(x0, y0, x1, y1);
        }
    }
}
