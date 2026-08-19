// Stage 4: UV island detection, wrap normalization, overlap merging, world-area metrics.
// 阶段4：UV岛检测、越界归一、重叠岛合并、真实面积统计。
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class IslandStage
    {
        /// <summary>Per-mapping mesh data cache. / 每映射的网格数据缓存。</summary>
        public class MeshUvData
        {
            public Vector2[] Uv;
            public int[] Indices;        // concatenated triangles of all submeshes / 全子网格三角形
            public int[] SubmeshOfTri;   // per-triangle submesh id / 每三角形所属子网格
            public Vector3[] Vertices;
        }

        public static readonly Dictionary<MappingKey, MeshUvData> UvCache = new Dictionary<MappingKey, MeshUvData>();

        public static void Run(AtoContext ctx)
        {
            using (AtoLog.Time("IslandStage", (l, ms) => ctx.Stats.StageTimes.Add((l, ms))))
            {
                AtoProgress.BeginStage(AtoL10n.Tr("stage.islands"));
                UvCache.Clear();
                int mi = 0;
                foreach (var kv in ctx.MappingTextures.ToList())
                {
                    AtoProgress.Step(mi++ / (float)Math.Max(1, ctx.MappingTextures.Count), kv.Key.ToString());
                    try
                    {
                        BuildIslands(ctx, kv.Key, kv.Value);
                    }
                    catch (AtoCancelledException) { throw; }
                    catch (Exception e)
                    {
                        AtoLog.Warn($"island build failed for {kv.Key}: {e.Message}; whitelisting its textures");
                        foreach (var t in kv.Value) ScanStage.MarkWhitelist(t, "island analysis failure");
                    }
                }
                ctx.Stats.IslandCount = ctx.Islands.Values.Sum(l => l.Count);
                AtoLog.Info($"islands: {ctx.Stats.IslandCount} across {ctx.Islands.Count} mappings");
            }
        }

        private static void BuildIslands(AtoContext ctx, MappingKey key, List<TexInfo> textures)
        {
            var mesh = key.Mesh;
            var uvList = new List<Vector2>();
            mesh.GetUVs(key.Channel, uvList);
            if (uvList.Count == 0)
            {
                foreach (var t in textures) ScanStage.MarkWhitelist(t, $"mesh has no uv{key.Channel}");
                return;
            }
            var uv = uvList.ToArray();

            // gather triangles / 收集三角形
            var indices = new List<int>();
            var subOfTri = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var tri = mesh.GetTriangles(s);
                for (int i = 0; i < tri.Length; i += 3)
                {
                    indices.Add(tri[i]); indices.Add(tri[i + 1]); indices.Add(tri[i + 2]);
                    subOfTri.Add(s);
                }
            }
            var idx = indices.ToArray();
            UvCache[key] = new MeshUvData
            {
                Uv = uv, Indices = idx, SubmeshOfTri = subOfTri.ToArray(), Vertices = mesh.vertices
            };

            // union-find over welded uv-vertices / 依据焊接UV顶点的并查集
            var weld = new Dictionary<(long, long), int>();
            var parent = new int[idx.Length / 3];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }

            var vertOwner = new Dictionary<int, int>(); // welded uv id -> triangle / 焊接点→三角形
            for (int t = 0; t < parent.Length; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    var p = uv[idx[t * 3 + k]];
                    var qk = ((long)Math.Round(p.x * 1e6), (long)Math.Round(p.y * 1e6));
                    if (!weld.TryGetValue(qk, out var wid)) { weld[qk] = wid = weld.Count; }
                    if (vertOwner.TryGetValue(wid, out var owner)) Union(owner, t);
                    else vertOwner[wid] = t;
                }
            }

            var groups = new Dictionary<int, Island>();
            for (int t = 0; t < parent.Length; t++)
            {
                int root = Find(t);
                if (!groups.TryGetValue(root, out var isl))
                    groups[root] = isl = new Island { Key = key };
                isl.Triangles.Add(t * 3);
                isl.SubmeshMask |= 1UL << Math.Min(subOfTri[t], 63);
            }

            // bbox + wrap normalization / 包围盒与越界归一
            var islands = new List<Island>();
            bool crossSeam = false;
            foreach (var isl in groups.Values)
            {
                Vector2 mn = new Vector2(float.MaxValue, float.MaxValue), mx = new Vector2(float.MinValue, float.MinValue);
                foreach (var t0 in isl.Triangles)
                    for (int k = 0; k < 3; k++)
                    {
                        var p = uv[idx[t0 + k]];
                        mn = Vector2.Min(mn, p); mx = Vector2.Max(mx, p);
                    }
                var size = mx - mn;
                const float eps = 1e-5f;
                if (size.x > 1f + eps || size.y > 1f + eps)
                { crossSeam = true; break; } // island wider than one tile -> depends on repeat / 依赖repeat
                // shift to [0,1] if not crossing an integer seam / 不跨缝则平移归一
                var shift = new Vector2(-Mathf.Floor(mn.x + eps), -Mathf.Floor(mn.y + eps));
                var smx = mx + shift;
                if (smx.x > 1f + eps || smx.y > 1f + eps) { crossSeam = true; break; }
                isl.Shift = shift;
                isl.BBoxMin = Vector2.Max(Vector2.zero, mn + shift);
                isl.BBoxMax = Vector2.Min(Vector2.one, mx + shift);
                isl.UvArea = UvArea(isl, uv, idx);
                islands.Add(isl);
            }

            if (crossSeam)
            {
                foreach (var t in textures) ScanStage.MarkWhitelist(t, "UV island crosses wrap seam");
                nadena.dev.ndmf.ErrorReport.ReportError(AtoL10n.Localizer,
                    nadena.dev.ndmf.ErrorSeverity.Information, "warn.uv_out_of_bounds",
                    mesh.name, key.Channel.ToString());
                return;
            }

            MergeOverlaps(islands, uv, idx, textures);
            ComputeWorldArea(ctx, key, islands);
            ctx.Islands[key] = islands;
            AtoLog.Debugf($"{key}: {islands.Count} islands after merge");
        }

        private static float UvArea(Island isl, Vector2[] uv, int[] idx)
        {
            double a = 0;
            foreach (var t0 in isl.Triangles)
            {
                var A = uv[idx[t0]]; var B = uv[idx[t0 + 1]]; var C = uv[idx[t0 + 2]];
                a += Math.Abs((B.x - A.x) * (C.y - A.y) - (C.x - A.x) * (B.y - A.y)) * 0.5;
            }
            return (float)a;
        }

        /// <summary>Merge islands whose rasters overlap (stacked/mirrored UVs). / 合并重叠岛。</summary>
        private static void MergeOverlaps(List<Island> islands, Vector2[] uv, int[] idx, List<TexInfo> textures)
        {
            int res = 256; // coarse merge grid / 粗合并网格
            var grids = new BitGrid[islands.Count];
            for (int i = 0; i < islands.Count; i++)
            {
                grids[i] = new BitGrid(res, res);
                foreach (var t0 in islands[i].Triangles)
                {
                    Vector2 A = (uv[idx[t0]] + islands[i].Shift) * res;
                    Vector2 B = (uv[idx[t0 + 1]] + islands[i].Shift) * res;
                    Vector2 C = (uv[idx[t0 + 2]] + islands[i].Shift) * res;
                    Raster.FillTriangle(grids[i], A, B, C);
                }
            }

            var merged = new bool[islands.Count];
            for (int i = 0; i < islands.Count; i++)
            {
                if (merged[i]) continue;
                for (int j = i + 1; j < islands.Count; j++)
                {
                    if (merged[j]) continue;
                    if (!(islands[i].BBoxMin.x <= islands[j].BBoxMax.x && islands[j].BBoxMin.x <= islands[i].BBoxMax.x &&
                          islands[i].BBoxMin.y <= islands[j].BBoxMax.y && islands[j].BBoxMin.y <= islands[i].BBoxMax.y))
                        continue;
                    int inter = 0, small = Math.Min(grids[i].CountBits(), grids[j].CountBits());
                    for (int w = 0; w < grids[i].Rows.Length; w++)
                        inter += BitGrid.PopCount(grids[i].Rows[w] & grids[j].Rows[w]);
                    if (small > 0 && inter / (float)small > 0.15f)
                    {
                        // merge j into i / 合并
                        islands[i].Triangles.AddRange(islands[j].Triangles);
                        islands[i].SubmeshMask |= islands[j].SubmeshMask;
                        islands[i].BBoxMin = Vector2.Min(islands[i].BBoxMin, islands[j].BBoxMin);
                        islands[i].BBoxMax = Vector2.Max(islands[i].BBoxMax, islands[j].BBoxMax);
                        islands[i].UvArea += islands[j].UvArea;
                        for (int w = 0; w < grids[i].Rows.Length; w++) grids[i].Rows[w] |= grids[j].Rows[w];
                        merged[j] = true;
                    }
                }
            }
            var kept = new List<Island>(islands.Count);
            for (int i = 0; i < islands.Count; i++)
                if (!merged[i]) kept.Add(islands[i]);
            islands.Clear();
            islands.AddRange(kept);
        }

        /// <summary>World area incl. blendshape & animated-scale inflation. / 含形态键与动画缩放的真实面积。</summary>
        private static void ComputeWorldArea(AtoContext ctx, MappingKey key, List<Island> islands)
        {
            var data = UvCache[key];
            float factor = 1f;
            Matrix4x4 l2w = Matrix4x4.identity;
            foreach (var ri in ctx.Renderers)
            {
                if (ri.Mesh != key.Mesh) continue;
                factor = Mathf.Max(factor, ri.BlendshapeAreaFactor * ri.MaxAnimScale * ri.MaxAnimScale);
                l2w = ri.Renderer.transform.localToWorldMatrix;
            }
            foreach (var isl in islands)
            {
                double area = 0;
                foreach (var t0 in isl.Triangles)
                {
                    var A = l2w.MultiplyPoint3x4(data.Vertices[data.Indices[t0]]);
                    var B = l2w.MultiplyPoint3x4(data.Vertices[data.Indices[t0 + 1]]);
                    var C = l2w.MultiplyPoint3x4(data.Vertices[data.Indices[t0 + 2]]);
                    area += Vector3.Cross(B - A, C - A).magnitude * 0.5f;
                }
                isl.WorldAreaMax = (float)(area * factor);
            }
        }
    }
}
