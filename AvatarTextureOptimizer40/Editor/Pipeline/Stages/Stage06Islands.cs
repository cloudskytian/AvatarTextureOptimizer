using System.Collections.Generic;
using Fosa.Ato.Editor.Analysis;
using Fosa.Ato.Editor.i18n;
using Fosa.Ato.Editor.Util;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.Ato.Editor.Pipeline.Stages
{
    /// <summary>
    /// Stage 06: For each eligible texture usage and its renderer's mesh submesh, extract UV islands
    /// (triangle connected components), normalize out-of-[0,1] UVs when safe, merge overlapping
    /// islands within the same texture, compute world area (blendshapes at 0/100, max anim scale),
    /// and map source pixel sizes. Whitelisted textures are skipped here.
    /// 阶段 06：对每个合格贴图使用与其渲染器网格子网格，提取 UV 岛（三角形连通分量），安全归一越界 UV，
    /// 合并同贴图内重叠岛，计算世界面积（形态键 0/100、最大动画缩放），映射源像素尺寸。
    /// </summary>
    internal sealed class Stage06Islands : IStage
    {
        public string Name => "ATO/06 Extracting UV islands";
        public float Weight => 4f;

        public void Run(AtoPipeline p)
        {
            var channelState = p.GetState<ChannelState>();
            var anim = p.GetState<AnimationState>();
            float maxScale = Mathf.Max(1f, anim.MaxScale);

            foreach (var slotKv in p.SlotTextures)
            {
                p.Progress.ThrowIfCancelled();
                var r = slotKv.Key.Renderer;
                int slot = slotKv.Key.SlotIndex;
                if (r == null) continue;
                Mesh m = r is SkinnedMeshRenderer smr ? smr.sharedMesh : (r as MeshRenderer)?.GetComponent<MeshFilter>()?.sharedMesh;
                if (m == null || slot >= m.subMeshCount) continue;
                int sub = slot;

                foreach (var u in slotKv.Value)
                {
                    if (u.Whitelisted) continue;
                    if (u.Texture == null) continue;
                    int ch = channelState.Get(u.Texture, u.ShaderPropertyName);
                    if (ch < 0 || ch >= 8) continue;

                    var uvs = GetUv(m, ch);
                    if (uvs == null) continue;
                    var tris = m.GetTriangles(sub);

                    // Safe normalization / 安全归一
                    if (!UvRasterizer.CanNormalize(uvs, tris, out var shift))
                    {
                        AtoLog.Warn(Localizer.T("warn.crossSeam", u.Texture.name));
                        u.Whitelisted = true; u.AtlasAllowed = false; p.Report.SkippedCount++;
                        continue;
                    }
                    if (shift != Vector2.zero)
                        for (int i = 0; i < uvs.Length; i++) uvs[i] += shift;

                    var components = FindComponents(uvs, tris);
                    float area = UvRasterizer.MaxWorldArea(m, sub, r.transform, maxScale);

                    foreach (var comp in components)
                    {
                        var island = BuildIsland(p, u, m, ch, sub, uvs, comp, area / components.Count);
                        if (island != null) p.Islands.Add(island);
                    }
                }
            }

            // Merge overlapping islands within same texture / 合并同贴图内重叠岛
            MergeOverlaps(p);

            p.Report.IslandCount = p.Islands.Count;
            AtoLog.VIf(p.Settings.VerboseLogging, $"Extracted {p.Islands.Count} islands.");
        }

        private static Vector2[] GetUv(Mesh m, int ch) => ch switch
        {
            0 => m.uv, 1 => m.uv2, 2 => m.uv3, 3 => m.uv4,
            4 => m.uv5, 5 => m.uv6, 6 => m.uv7, 7 => m.uv8,
            _ => null,
        };

        private static List<List<int>> FindComponents(Vector2[] uvs, int[] tris)
        {
            // Union-find over vertices referenced by triangles / 按顶点并查集
            var parent = new Dictionary<int, int>();
            int Root(int x) { if (!parent.ContainsKey(x)) parent[x] = x; while (parent[x] != x) parent[x] = parent[parent[x]]; x = parent[x]; return x; }
            void Union(int a, int b) { int ra = Root(a), rb = Root(b); if (ra != rb) parent[ra] = rb; }

            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int v0 = tris[i], v1 = tris[i + 1], v2 = tris[i + 2];
                Union(v0, v1); Union(v1, v2);
                int root = Root(v0);
                if (!groups.TryGetValue(root, out var list)) groups[root] = list = new List<int>();
                list.Add(v0); list.Add(v1); list.Add(v2);
            }

            // Now group triangles by shared vertex-set root. Note triangles in a connected mesh may all
            // be one component even if UV islands are disjoint in UV space; we must split by UV
            // adjacency instead — two triangles are same island if they share an EDGE and that edge's
            // UVs match. 进一步按 UV 边连接拆分
            var edgeMap = new Dictionary<ulong, List<int>>();
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = tris[i + e], b = tris[i + (e + 1) % 3];
                    if (uvs[a] == uvs[b]) continue; // degenerate / 退化
                    ulong key = EdgeKey(a, b);
                    if (!edgeMap.TryGetValue(key, out var l)) edgeMap[key] = l = new List<int>();
                    l.Add(i / 3);
                }
            }
            var tparent = new int[tris.Length / 3];
            for (int i = 0; i < tparent.Length; i++) tparent[i] = i;
            int TRoot(int x) { while (tparent[x] != x) { tparent[x] = tparent[tparent[x]]; x = tparent[x]; } return x; }
            foreach (var l in edgeMap.Values)
                for (int i = 1; i < l.Count; i++)
                {
                    int ra = TRoot(l[0]), rb = TRoot(l[i]);
                    if (ra != rb) tparent[ra] = rb;
                }

            var compMap = new Dictionary<int, List<int>>();
            for (int i = 0; i < tparent.Length; i++)
            {
                int r = TRoot(i);
                if (!compMap.TryGetValue(r, out var list)) compMap[r] = list = new List<int>();
                list.Add(tris[i * 3]); list.Add(tris[i * 3 + 1]); list.Add(tris[i * 3 + 2]);
            }
            return new List<List<int>>(compMap.Values);
        }

        private static ulong EdgeKey(int a, int b)
        {
            if (a > b) (a, b) = (b, a);
            return ((ulong)a << 32) | (uint)b;
        }

        private static Island BuildIsland(AtoPipeline p, TextureUsage u, Mesh m, int ch, int sub,
            Vector2[] uvs, List<int> verts, float worldArea)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < verts.Count; i += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    var v = uvs[verts[i + k]];
                    if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x;
                    if (v.y < minY) minY = v.y; if (v.y > maxY) maxY = v.y;
                }
            }
            var box = Rect.MinMaxRect(minX, minY, maxX, maxY);
            if (box.width <= 0 || box.height <= 0) return null;

            var tex = u.Texture;
            var island = new Island
            {
                SourceTexture = tex,
                SourceUsage = u,
                Uv = new UvChannelRef(m, ch, sub),
                Triangles = verts,
                UvBox = box,
                SizePx = new Vector2(box.width * tex.width, box.height * tex.height),
                WorldArea = Mathf.Max(1e-6f, worldArea),
                UvToPx = new Matrix2x3 { m00 = tex.width, m11 = tex.height },
            };
            // Solid-color detection deferred to quality stage (needs decoded pixels).
            return island;
        }

        private static void MergeOverlaps(AtoPipeline p)
        {
            // Islands from the same texture whose UV boxes overlap are merged (per spec).
            // 同贴图内 UV 包围盒重叠的岛予以合并
            var byTex = new Dictionary<Texture2D, List<Island>>();
            foreach (var isl in p.Islands)
            {
                if (isl.SourceTexture == null) continue;
                if (!byTex.TryGetValue(isl.SourceTexture, out var list)) byTex[isl.SourceTexture] = list = new List<Island>();
                list.Add(isl);
            }
            int merged = 0;
            foreach (var kv in byTex)
            {
                var list = kv.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (!list[i].UvBox.Overlaps(list[j].UvBox)) continue;
                        // Union triangles + bbox / 合并三角形与包围盒
                        list[i].Triangles.AddRange(list[j].Triangles);
                        list[i].UvBox = Rect.MinMaxRect(
                            Mathf.Min(list[i].UvBox.xMin, list[j].UvBox.xMin),
                            Mathf.Min(list[i].UvBox.yMin, list[j].UvBox.yMin),
                            Mathf.Max(list[i].UvBox.xMax, list[j].UvBox.xMax),
                            Mathf.Max(list[i].UvBox.yMax, list[j].UvBox.yMax));
                        list[i].OverlapsOther = true;
                        list[j].Triangles.Clear();
                        merged++;
                    }
                }
            }
            if (merged > 0)
            {
                p.Islands.RemoveAll(i => i.Triangles.Count == 0);
                AtoLog.VIf(p.Settings.VerboseLogging, $"Merged {merged} overlapping island(s).");
            }
        }
    }
}
