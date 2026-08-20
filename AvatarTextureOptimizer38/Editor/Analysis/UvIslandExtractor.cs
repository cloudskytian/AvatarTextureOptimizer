using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Extracts UV islands, wrap-normalizes, merges overlaps. / 提取 UV 岛、wrap 归一、合并重叠。
    /// Multi-channel UVs are independent. / 多通道 UV 互相独立。
    /// </summary>
    public static class UvIslandExtractor
    {
        private const float UvEps = 1e-5f;

        public static List<UvIsland> Extract(RendererRef rr, int uvChannel, Texture2D tex, out string wrapWarning)
        {
            wrapWarning = null;
            var mesh = rr.Mesh;
            var result = new List<UvIsland>();
            if (mesh == null) return result;
            var uvs = MeshUvUtil.GetUv(mesh, uvChannel);
            if (uvs == null || uvs.Length == 0) return result;
            var verts = mesh.vertices;
            var bind = mesh.bindposes;
            // World scale applied later via MaxScaleMul. / 世界缩放稍后用 MaxScaleMul 乘。

            int subCount = mesh.subMeshCount;
            for (int sm = 0; sm < subCount; sm++)
            {
                var tris = mesh.GetTriangles(sm);
                if (tris == null || tris.Length < 3) continue;
                int triCount = tris.Length / 3;
                var parent = new int[triCount];
                for (int i = 0; i < triCount; i++) parent[i] = i;

                int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
                void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }

                var edgeMap = new Dictionary<long, int>();
                bool wrapCross = false;
                for (int t = 0; t < triCount; t++)
                {
                    int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                    if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
                    if (EdgeWrap(uvs[i0], uvs[i1]) || EdgeWrap(uvs[i1], uvs[i2]) || EdgeWrap(uvs[i2], uvs[i0]))
                        wrapCross = true;
                    AddEdge(i0, i1, t);
                    AddEdge(i1, i2, t);
                    AddEdge(i2, i0, t);
                }

                void AddEdge(int a, int b, int tri)
                {
                    if (a > b) { var tmp = a; a = b; b = tmp; }
                    long key = ((long)a << 32) ^ (uint)b;
                    // Also key by UV positions so welded UV edges connect. / 同时按 UV 位置焊接。
                    var ua = Quant(uvs[a]);
                    var ub = Quant(uvs[b]);
                    long uk = ua < ub ? (ua << 32) ^ (uint)ub : (ub << 32) ^ (uint)ua;
                    if (edgeMap.TryGetValue(uk, out var other)) Union(tri, other);
                    else edgeMap[uk] = tri;
                }

                if (wrapCross)
                {
                    wrapWarning = $"mesh {mesh.name} uv{uvChannel} submesh {sm} crosses wrap seam";
                    continue;
                }

                var groups = new Dictionary<int, List<int>>();
                for (int t = 0; t < triCount; t++)
                {
                    int r = Find(t);
                    if (!groups.TryGetValue(r, out var list)) { list = new List<int>(); groups[r] = list; }
                    list.Add(t);
                }

                foreach (var g in groups.Values)
                {
                    var island = BuildIsland(rr, mesh, uvs, verts, tris, g, uvChannel, sm, tex, out var wrapFail);
                    if (wrapFail)
                    {
                        wrapWarning = $"mesh {mesh.name} uv{uvChannel} island cannot normalize to [0,1]";
                        continue;
                    }
                    if (island != null) result.Add(island);
                }
            }

            MergeOverlapping(result, tex);
            return result;
        }

        private static bool EdgeWrap(Vector2 a, Vector2 b)
        {
            return Mathf.Abs(a.x - b.x) > 0.5f || Mathf.Abs(a.y - b.y) > 0.5f;
        }

        private static long Quant(Vector2 uv)
        {
            int x = (int)Math.Round(uv.x * 1048576.0);
            int y = (int)Math.Round(uv.y * 1048576.0);
            return ((long)x << 32) ^ (uint)y;
        }

        private static UvIsland BuildIsland(RendererRef rr, Mesh mesh, Vector2[] uvs, Vector3[] verts, int[] tris,
            List<int> triGroup, int uvCh, int sm, Texture2D tex, out bool wrapFail)
        {
            wrapFail = false;
            float minU = float.PositiveInfinity, minV = float.PositiveInfinity;
            float maxU = float.NegativeInfinity, maxV = float.NegativeInfinity;
            var vset = new HashSet<int>();
            float uvArea = 0f, worldArea = 0f;
            foreach (var t in triGroup)
            {
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                vset.Add(i0); vset.Add(i1); vset.Add(i2);
                var u0 = uvs[i0]; var u1 = uvs[i1]; var u2 = uvs[i2];
                minU = Mathf.Min(minU, u0.x, u1.x, u2.x);
                minV = Mathf.Min(minV, u0.y, u1.y, u2.y);
                maxU = Mathf.Max(maxU, u0.x, u1.x, u2.x);
                maxV = Mathf.Max(maxV, u0.y, u1.y, u2.y);
                uvArea += Mathf.Abs(Cross(u1 - u0, u2 - u0)) * 0.5f;
                if (i0 < verts.Length && i1 < verts.Length && i2 < verts.Length)
                    worldArea += Mathf.Abs(Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]).magnitude) * 0.5f;
            }

            float spanU = maxU - minU, spanV = maxV - minV;
            if (spanU > 1f + 1e-3f || spanV > 1f + 1e-3f)
            {
                wrapFail = true;
                return null;
            }

            var translate = new Vector2(-Mathf.Floor(minU + 1e-6f), -Mathf.Floor(minV + 1e-6f));
            minU += translate.x; maxU += translate.x;
            minV += translate.y; maxV += translate.y;
            if (minU < -1e-3f || minV < -1e-3f || maxU > 1f + 1e-3f || maxV > 1f + 1e-3f)
            {
                wrapFail = true;
                return null;
            }

            worldArea *= rr.MaxScaleMul * rr.MaxScaleMul;
            worldArea = Mathf.Max(worldArea, BlendshapeMaxArea(mesh, triGroup, tris, verts) * rr.MaxScaleMul * rr.MaxScaleMul);

            int tw = tex != null ? tex.width : 1024;
            int th = tex != null ? tex.height : 1024;
            int px0 = Mathf.Clamp(Mathf.FloorToInt(minU * tw), 0, tw - 1);
            int py0 = Mathf.Clamp(Mathf.FloorToInt(minV * th), 0, th - 1);
            int px1 = Mathf.Clamp(Mathf.CeilToInt(maxU * tw), 1, tw);
            int py1 = Mathf.Clamp(Mathf.CeilToInt(maxV * th), 1, th);
            int pw = Math.Max(1, px1 - px0);
            int ph = Math.Max(1, py1 - py0);

            var island = new UvIsland
            {
                Mesh = mesh,
                Owner = rr,
                UvChannel = uvCh,
                Submesh = sm,
                UvMin = new Vector2(minU, minV),
                UvMax = new Vector2(maxU, maxV),
                UvTranslate = translate,
                WorldArea = worldArea,
                UvArea = Mathf.Max(uvArea, 1e-12f),
                OrigPixelW = pw,
                OrigPixelH = ph,
                VertexIndices = new List<int>(vset),
                TriangleIndices = new List<int>(triGroup)
            };

            island.Shape = Rasterize(uvs, tris, triGroup, minU, minV, maxU, maxV, tw, th);
            DetectSolidAndAnisotropy(island, tex, tw, th, px0, py0, pw, ph);
            return island;
        }

        private static float BlendshapeMaxArea(Mesh mesh, List<int> triGroup, int[] tris, Vector3[] verts)
        {
            if (mesh.blendShapeCount == 0) return 0f;
            float max = 0f;
            var deltaV = new Vector3[verts.Length];
            var deltaN = new Vector3[verts.Length];
            var deltaT = new Vector3[verts.Length];
            for (int b = 0; b < mesh.blendShapeCount; b++)
            {
                int frames = mesh.GetBlendShapeFrameCount(b);
                if (frames <= 0) continue;
                // Only 0 and 100 (or last frame if named 100). / 仅取 0 与 100。
                float lastW = mesh.GetBlendShapeFrameWeight(b, frames - 1);
                mesh.GetBlendShapeFrameVertices(b, frames - 1, deltaV, deltaN, deltaT);
                float area = 0f;
                foreach (var t in triGroup)
                {
                    int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                    if (i0 >= verts.Length) continue;
                    var p0 = verts[i0] + deltaV[i0];
                    var p1 = verts[i1] + deltaV[i1];
                    var p2 = verts[i2] + deltaV[i2];
                    area += Mathf.Abs(Vector3.Cross(p1 - p0, p2 - p0).magnitude) * 0.5f;
                }
                max = Mathf.Max(max, area);
                AtoLog.VerboseLog($"blendshape {mesh.GetBlendShapeName(b)} lastWeight={lastW} area={area:F6}");
            }
            return max;
        }

        private static Bitmask2D Rasterize(Vector2[] uvs, int[] tris, List<int> triGroup,
            float minU, float minV, float maxU, float maxV, int tw, int th)
        {
            int gw = Math.Max(1, (int)Math.Ceiling(Math.Max(1, (maxU - minU) * tw) / 4.0));
            int gh = Math.Max(1, (int)Math.Ceiling(Math.Max(1, (maxV - minV) * th) / 4.0));
            var mask = Bitmask2D.Create(gw, gh);
            float su = (maxU - minU) < 1e-8f ? 1f : (maxU - minU);
            float sv = (maxV - minV) < 1e-8f ? 1f : (maxV - minV);
            foreach (var t in triGroup)
            {
                var a = uvs[tris[t * 3]];
                var b = uvs[tris[t * 3 + 1]];
                var c = uvs[tris[t * 3 + 2]];
                var pa = new Vector2((a.x - minU) / su * gw, (a.y - minV) / sv * gh);
                var pb = new Vector2((b.x - minU) / su * gw, (b.y - minV) / sv * gh);
                var pc = new Vector2((c.x - minU) / su * gw, (c.y - minV) / sv * gh);
                FillTri(mask, pa, pb, pc);
            }
            return mask;
        }

        private static void FillTri(Bitmask2D m, Vector2 a, Vector2 b, Vector2 c)
        {
            int minx = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, b.x, c.x)), 0, m.Width - 1);
            int maxx = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, b.x, c.x)), 0, m.Width - 1);
            int miny = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, b.y, c.y)), 0, m.Height - 1);
            int maxy = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, b.y, c.y)), 0, m.Height - 1);
            for (int y = miny; y <= maxy; y++)
            for (int x = minx; x <= maxx; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                if (Inside(p, a, b, c)) m.Set(x, y);
            }
        }

        private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float s = Sign(p, a, b);
            float t = Sign(p, b, c);
            float u = Sign(p, c, a);
            bool neg = s < 0 || t < 0 || u < 0;
            bool pos = s > 0 || t > 0 || u > 0;
            return !(neg && pos);
        }

        private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3) =>
            (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private static void DetectSolidAndAnisotropy(UvIsland island, Texture2D tex, int tw, int th,
            int px0, int py0, int pw, int ph)
        {
            if (tex == null) return;
            var px = TextureDecodeCache.GetPixels(tex, out _, out _);
            bool first = true;
            Color32 c0 = default;
            bool solid = true;
            long sum = 0;
            int n = 0;
            for (int y = 0; y < ph; y++)
            {
                int yy = Mathf.Clamp(py0 + y, 0, th - 1);
                for (int x = 0; x < pw; x++)
                {
                    int xx = Mathf.Clamp(px0 + x, 0, tw - 1);
                    var c = px[yy * tw + xx];
                    if (first) { c0 = c; first = false; }
                    else if (c.r != c0.r || c.g != c0.g || c.b != c0.b || c.a != c0.a) solid = false;
                    sum += c.r + c.g + c.b;
                    n++;
                }
            }
            island.SolidColor = solid;
            island.SolidColorValue = c0;
            island.Anisotropic = pw > ph * 1.5f || ph > pw * 1.5f;
        }

        private static void MergeOverlapping(List<UvIsland> islands, Texture2D tex)
        {
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < islands.Count; i++)
                for (int j = i + 1; j < islands.Count; j++)
                {
                    if (islands[i].Owner != islands[j].Owner || islands[i].UvChannel != islands[j].UvChannel) continue;
                    if (!Overlap(islands[i], islands[j])) continue;
                    islands[i].UvMin = Vector2.Min(islands[i].UvMin, islands[j].UvMin);
                    islands[i].UvMax = Vector2.Max(islands[i].UvMax, islands[j].UvMax);
                    islands[i].WorldArea += islands[j].WorldArea;
                    islands[i].UvArea += islands[j].UvArea;
                    islands[i].VertexIndices.AddRange(islands[j].VertexIndices);
                    islands[i].TriangleIndices.AddRange(islands[j].TriangleIndices);
                    islands[i].OrigPixelW = Mathf.Max(islands[i].OrigPixelW, Mathf.CeilToInt((islands[i].UvMax.x - islands[i].UvMin.x) * (tex != null ? tex.width : 1)));
                    islands[i].OrigPixelH = Mathf.Max(islands[i].OrigPixelH, Mathf.CeilToInt((islands[i].UvMax.y - islands[i].UvMin.y) * (tex != null ? tex.height : 1)));
                    islands.RemoveAt(j);
                    merged = true;
                    goto NEXT;
                }
                NEXT: ;
            }
        }

        private static bool Overlap(UvIsland a, UvIsland b)
        {
            return a.UvMin.x < b.UvMax.x && a.UvMax.x > b.UvMin.x &&
                   a.UvMin.y < b.UvMax.y && a.UvMax.y > b.UvMin.y;
        }
    }
}
