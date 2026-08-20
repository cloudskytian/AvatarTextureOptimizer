using System;
using System.Collections.Generic;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class UvGroupBuilder
    {
        public static List<UvGroup> Build(List<SlotBinding> bindings, List<Renderer> renderers, AnimationImpact anim, HashSet<Texture> whitelist, BakeReport report)
        {
            var groups = new Dictionary<string, UvGroup>();
            foreach (var b in bindings)
            {
                if (b.Mesh == null || b.Tex.Texture == null) continue;
                string id = b.Mesh.GetInstanceID() + ":" + b.Tex.UvChannel + ":" + b.Slot;
                if (!groups.TryGetValue(id, out var g))
                {
                    g = new UvGroup
                    {
                        Id = id,
                        UvChannel = b.Tex.UvChannel,
                        SourceMesh = b.Mesh,
                        SourceRenderer = b.Renderer
                    };
                    groups[id] = g;
                    ExtractIslands(g, b.Mesh, b.Tex.UvChannel, b.Tex.Texture, report);
                }
                if (!g.Textures.Contains(b.Tex.Texture))
                {
                    g.Textures.Add(b.Tex.Texture);
                    g.Semantics.Add(b.Tex.Semantic);
                }
                if (b.Whitelisted || whitelist.Contains(b.Tex.Texture)) g.Whitelisted = true;
                if (b.Alpha > g.StrictestAlpha) g.StrictestAlpha = b.Alpha;
                if (b.Cutoff > g.StrictestCutoff) g.StrictestCutoff = b.Cutoff;

                if (g.CrossesWrapSeam)
                {
                    g.Whitelisted = true;
                    report.Warnings.Add("UV cross-seam " + b.Mesh.name + " uv" + b.Tex.UvChannel);
                    AtoLog.Warn("UV crosses wrap seam; whitelist " + b.Mesh.name);
                }
            }

            // Type groups
            var typeMap = new Dictionary<string, TextureTypeGroup>();
            foreach (var g in groups.Values)
            {
                bool hasN = g.Semantics.Contains(AtoTextureSemantic.Normal);
                bool hasM = g.Semantics.Contains(AtoTextureSemantic.Mask) || g.Semantics.Contains(AtoTextureSemantic.MetallicGloss);
                var filter = g.Textures.Count > 0 ? g.Textures[0].filterMode : FilterMode.Bilinear;
                bool srgb = g.Semantics.Contains(AtoTextureSemantic.Albedo);
                string tid = hasN + "|" + hasM + "|" + srgb + "|" + filter;
                if (!typeMap.TryGetValue(tid, out var tg))
                {
                    tg = new TextureTypeGroup { Id = tid, HasNormal = hasN, HasMask = hasM, Srgb = srgb, Filter = filter };
                    typeMap[tid] = tg;
                }
                tg.Members.Add(g);
                g.TypeGroup = tg;
            }

            AtoLog.Info($"Type groups={typeMap.Count}");
            report.IslandCount = groups.Values.Sum(g => g.Islands.Count);
            return groups.Values.ToList();
        }

        static void ExtractIslands(UvGroup g, Mesh mesh, int channel, Texture2D tex, BakeReport report)
        {
            var uvs = new List<Vector2>();
            mesh.GetUVs(channel, uvs);
            if (uvs.Count == 0)
            {
                g.Whitelisted = true;
                return;
            }

            float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
            foreach (var uv in uvs)
            {
                minU = Mathf.Min(minU, uv.x); minV = Mathf.Min(minV, uv.y);
                maxU = Mathf.Max(maxU, uv.x); maxV = Mathf.Max(maxV, uv.y);
            }
            float spanU = maxU - minU, spanV = maxV - minV;
            bool outOf01 = minU < -1e-4f || minV < -1e-4f || maxU > 1.0001f || maxV > 1.0001f;
            bool crosses = spanU > 1.0001f || spanV > 1.0001f;
            if (outOf01 && crosses)
            {
                g.CrossesWrapSeam = true;
                return;
            }
            if (outOf01 && !crosses)
            {
                g.NeedsNormalize = true;
                g.NormalizeOffset = new Vector2(Mathf.Floor(minU), Mathf.Floor(minV));
            }

            var tris = mesh.triangles;
            int n = tris.Length / 3;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

            var edge = new Dictionary<long, int>();
            for (int t = 0; t < n; t++)
            {
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                Connect(i0, i1, t); Connect(i1, i2, t); Connect(i2, i0, t);
            }
            void Connect(int a, int b, int tri)
            {
                if (a > b) { var tmp = a; a = b; b = tmp; }
                long k = ((long)a << 32) ^ (uint)b;
                if (edge.TryGetValue(k, out int o)) Union(o, tri);
                else edge[k] = tri;
            }

            var buckets = new Dictionary<int, UvIsland>();
            var verts = mesh.vertices;
            for (int t = 0; t < n; t++)
            {
                int r = Find(t);
                if (!buckets.TryGetValue(r, out var isl))
                {
                    isl = new UvIsland { MeshId = mesh.GetInstanceID(), UvChannel = channel };
                    buckets[r] = isl;
                }
                isl.TriangleIndices.Add(t);
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                var u0 = uvs[i0] - g.NormalizeOffset;
                var u1 = uvs[i1] - g.NormalizeOffset;
                var u2 = uvs[i2] - g.NormalizeOffset;
                Encaps(ref isl.Bounds01, u0); Encaps(ref isl.Bounds01, u1); Encaps(ref isl.Bounds01, u2);
                isl.WorldArea += Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]).magnitude * 0.5f;
            }

            // merge overlapping islands in texture space
            var list = buckets.Values.ToList();
            for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
            {
                if (list[i].Bounds01.Overlaps(list[j].Bounds01))
                {
                    list[i].TriangleIndices.AddRange(list[j].TriangleIndices);
                    var u = list[i].Bounds01;
                    u.xMin = Mathf.Min(u.xMin, list[j].Bounds01.xMin);
                    u.yMin = Mathf.Min(u.yMin, list[j].Bounds01.yMin);
                    u.xMax = Mathf.Max(u.xMax, list[j].Bounds01.xMax);
                    u.yMax = Mathf.Max(u.yMax, list[j].Bounds01.yMax);
                    list[i].Bounds01 = u;
                    list[i].WorldArea += list[j].WorldArea;
                    list.RemoveAt(j);
                    j--;
                }
            }

            int tw = tex.width, th = tex.height;
            foreach (var isl in list)
            {
                isl.PixelBounds = new RectInt(
                    Mathf.FloorToInt(isl.Bounds01.xMin * tw),
                    Mathf.FloorToInt(isl.Bounds01.yMin * th),
                    Mathf.Max(1, Mathf.CeilToInt(isl.Bounds01.width * tw)),
                    Mathf.Max(1, Mathf.CeilToInt(isl.Bounds01.height * th)));
                float du = Mathf.Max(1e-6f, isl.Bounds01.width);
                float dv = Mathf.Max(1e-6f, isl.Bounds01.height);
                isl.Anisotropy = du / dv;
                g.Islands.Add(isl);
            }

            // blend shapes 0 vs 100
            if (mesh.blendShapeCount > 0)
            {
                var dV = new Vector3[mesh.vertexCount];
                var dN = new Vector3[mesh.vertexCount];
                var dT = new Vector3[mesh.vertexCount];
                float extra = 0f;
                for (int s = 0; s < mesh.blendShapeCount; s++)
                {
                    int frames = mesh.GetBlendShapeFrameCount(s);
                    if (frames == 0) continue;
                    mesh.GetBlendShapeFrameVertices(s, frames - 1, dV, dN, dT);
                    float area = 0;
                    for (int t = 0; t < n; t++)
                    {
                        int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                        var v0 = verts[i0] + dV[i0];
                        var v1 = verts[i1] + dV[i1];
                        var v2 = verts[i2] + dV[i2];
                        area += Vector3.Cross(v1 - v0, v2 - v0).magnitude * 0.5f;
                    }
                    extra = Mathf.Max(extra, area);
                }
                if (extra > 0)
                {
                    float baseA = g.Islands.Sum(i => i.WorldArea);
                    if (baseA > 1e-8f)
                    {
                        float k = extra / baseA;
                        foreach (var isl in g.Islands) isl.WorldArea *= Mathf.Max(1f, k);
                    }
                }
            }
        }

        static void Encaps(ref Rect r, Vector2 p)
        {
            if (r.width == 0 && r.height == 0 && r.x == 0 && r.y == 0)
            {
                r = new Rect(p.x, p.y, 0, 0);
                return;
            }
            float xMin = Mathf.Min(r.xMin, p.x);
            float yMin = Mathf.Min(r.yMin, p.y);
            float xMax = Mathf.Max(r.xMax, p.x);
            float yMax = Mathf.Max(r.yMax, p.y);
            r = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }
    }
}
