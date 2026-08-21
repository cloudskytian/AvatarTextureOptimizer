using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace net.fosa.ato
{
    /// <summary>
    /// UV 岛提取 / UV island extraction.
    ///
    ///  * 对每个网格的每个 UV 通道(0-3)按三角形 UV 连通性拆岛 / splits each mesh's UV channels (0-3) into islands by UV connectivity;
    ///  * 越界 UV: 若能整体平移归一到 [0,1] 且不跨 wrap 缝则归一重映射; 跨缝的岛视作白名单并 warning
    ///    / out-of-bounds UVs are shift-normalized when possible; wrap-crossing islands are whitelisted with a warning;
    ///  * 同一贴图内重叠岛合并(并查集) / overlapping islands within the same texture are merged (union-find);
    ///  * 建立 岛 <-> 贴图 映射(UV 组), 白名单贴图同样入组以传播"同UV跳过图集化"规则
    ///    / builds island <-> texture mappings (UV groups); whitelisted textures also join so the
    ///    "UV sharers skip atlasing" rule can propagate;
    ///  * 计算岛面积: 基础面积 + 形态键(每键取 0 与 100 的最大值) + 动画缩放(取最大值)
    ///    / computes island area: base + blendshapes (max of weight 0 vs 100 per shape) + animated scale (max);
    ///  * 检测贴图是否含 alpha 通道 / detects whether textures carry an alpha channel.
    /// </summary>
    internal static class ATOIslands
    {
        public static void Run(ATOBuildState state, GameObject avatarRoot)
        {
            Profiler.BeginSample("ATO.Islands");
            var timer = new ATOLog.StageTimer();
            timer.Start();
            var anim = state.anim;

            // 1. 形态键与缩放因子 / blendshape deltas & animated scale factors
            timer.BeginStep("meshData");
            foreach (var mi in state.meshes)
            {
                mi.blendShapeDeltas = ReadBlendShapeDeltas(mi.mesh);
                mi.hasBlendShapes = mi.blendShapeDeltas != null && mi.blendShapeDeltas.Count > 0;
                mi.animatedScaleFactor = ComputeAnimatedScaleFactor(mi.renderer.transform, anim);
            }

            timer.EndStep();

            // 2. 拆岛 + 归一 + 贴图映射 / island extraction + normalization + texture mapping
            timer.BeginStep("extractIslands");
            foreach (var mi in state.meshes)
            {
                ExtractIslands(state, mi);
            }

            timer.EndStep();

            // 3. 重叠岛合并(按贴图) / overlapping island merge (per texture)
            timer.BeginStep("mergeOverlaps");
            MergeOverlappingIslands(state);
            timer.EndStep();

            // 4. 面积计算 / area computation
            timer.BeginStep("areas");
            foreach (var mi in state.meshes)
            {
                ComputeAreas(mi);
            }

            timer.EndStep();

            // 5. 白名单同UV传播 + 统计 / whitelist UV-sharing propagation + stats
            timer.BeginStep("propagateWhitelist");
            PropagateWhitelistSharing(state);
            timer.EndStep();

            // 6. alpha 检测 / alpha detection
            timer.BeginStep("alphaDetect");
            DetectAlpha(state);
            timer.EndStep();

            // 7. 灰度贴图被使用通道分析 / used-channel analysis for grayscale textures
            timer.BeginStep("usedChannels");
            ATOUsedChannels.Analyze(state);
            timer.EndStep();

            timer.End("UV岛 UV Islands");
            Profiler.EndSample();
        }

        // ------------------------------------------------------------------
        private static List<(string name, Vector3[] delta)> ReadBlendShapeDeltas(Mesh mesh)
        {
            if (mesh == null || mesh.blendShapeCount == 0) return null;
            var list = new List<(string, Vector3[])>();
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                int frameCount = mesh.GetBlendShapeFrameCount(i);
                if (frameCount == 0) continue;
                // 取权重最大的帧(通常 100) / use the frame with the largest weight (usually 100)
                var delta = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(i, frameCount - 1, delta, null, null);
                list.Add((mesh.GetBlendShapeName(i), delta));
            }

            return list;
        }

        private static float ComputeAnimatedScaleFactor(Transform t, ATOAnimAnalysis anim)
        {
            float factor = 1f;
            var cur = t;
            while (cur != null)
            {
                if (anim.scaleBindings.TryGetValue(cur, out var bindings))
                {
                    float mx = Mathf.Abs(cur.localScale.x), my = Mathf.Abs(cur.localScale.y), mz = Mathf.Abs(cur.localScale.z);
                    foreach (var b in bindings)
                    {
                        // 通过属性名取该轴曲线最大值 / per-axis max via property name
                        foreach (var rec in anim.scaleRecords)
                        {
                            if (rec.Binding.path != b.path || rec.Binding.propertyName != b.propertyName) continue;
                            var curve = AnimationUtility.GetEditorCurve(rec.Clip, b);
                            if (curve == null) continue;
                            foreach (var k in curve.keys)
                            {
                                float v = Mathf.Abs(k.value);
                                if (b.propertyName.EndsWith(".x")) mx = Mathf.Max(mx, v);
                                else if (b.propertyName.EndsWith(".y")) my = Mathf.Max(my, v);
                                else if (b.propertyName.EndsWith(".z")) mz = Mathf.Max(mz, v);
                            }
                        }
                    }

                    float curProd = Mathf.Max(Mathf.Abs(cur.localScale.x) * Mathf.Abs(cur.localScale.y),
                        Mathf.Max(Mathf.Abs(cur.localScale.y) * Mathf.Abs(cur.localScale.z),
                            Mathf.Abs(cur.localScale.z) * Mathf.Abs(cur.localScale.x)));
                    float maxProd = Mathf.Max(mx * my, Mathf.Max(my * mz, mz * mx));
                    if (curProd > 1e-6f) factor *= maxProd / curProd;
                }

                cur = cur.parent;
            }

            return factor;
        }

        // ------------------------------------------------------------------
        private static void ExtractIslands(ATOBuildState state, ATOMeshInfo mi)
        {
            var mesh = mi.mesh;
            int[] tris = mesh.triangles;
            int triCount = tris.Length / 3;

            // 子网格三角形区间 / submesh triangle ranges
            var submeshRanges = new List<(int start, int end)>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var sm = mesh.GetSubMesh(s);
                submeshRanges.Add((sm.indexStart / 3, (sm.indexStart + sm.indexCount) / 3));
            }

            for (int channel = 0; channel < 4; channel++)
            {
                if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + channel)) continue;
                var uvList = new List<Vector2>();
                mesh.GetUVs(channel, uvList);
                if (uvList.Count == 0) continue;

                // 当前通道的工作UV(后续归一/图集化会修改) / working UV list for this channel
                mi.newUVs[channel] = new List<Vector2>(uvList);

                // 并查集: (顶点, uv) -> 组件 / union-find over (vertex, uv) corners
                var parent = new int[triCount * 3];
                for (int i = 0; i < parent.Length; i++) parent[i] = i;

                var uvLookup = new Dictionary<(int v, float u, float vv), int>();
                for (int t = 0; t < triCount; t++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int vtx = tris[t * 3 + c];
                        var uv = uvList[vtx];
                        var key = (vtx, uv.x, uv.y);
                        if (uvLookup.TryGetValue(key, out int existing))
                        {
                            Union(parent, t * 3 + c, existing);
                        }
                        else
                        {
                            uvLookup[key] = t * 3 + c;
                        }
                    }
                }

                var triByRoot = new Dictionary<int, List<int>>();
                for (int t = 0; t < triCount; t++)
                {
                    int root = Find(parent, t * 3);
                    if (!triByRoot.TryGetValue(root, out var list)) triByRoot[root] = list = new List<int>();
                    list.Add(t);
                }

                foreach (var kv in triByRoot)
                {
                    var island = new ATOIsland
                    {
                        owner = mi,
                        channel = channel,
                        triangles = kv.Value.ToArray()
                    };

                    // 岛内顶点 / island vertices
                    var verts = new HashSet<int>();
                    foreach (var t in kv.Value)
                    {
                        for (int c = 0; c < 3; c++) verts.Add(tris[t * 3 + c]);
                    }

                    island.vertexCount = verts.Count;

                    // 包围盒 / bounds
                    Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
                    foreach (var v in verts)
                    {
                        var uv = uvList[v];
                        min = Vector2.Min(min, uv);
                        max = Vector2.Max(max, uv);
                    }

                    island.uvBounds = new Rect(min, max - min);

                    // 越界归一 / out-of-bounds normalization
                    if (!TryNormalize(mi, island, verts, uvList))
                    {
                        ATOLog.Warn($"UV岛越界且跨wrap缝, 其贴图视作白名单跳过 / out-of-bounds island crossing the wrap seam (mesh {mi.renderer.name}, ch{channel}); its textures are whitelisted");
                        MarkIslandTexturesWhitelist(state, mi, island, ATOSkipReason.WrapCrossSeam, "UV crosses wrap seam");
                        continue;
                    }

                    mi.islands.Add(island);
                    state.islandCount++;
                }
            }

            // 贴图映射 / texture mapping
            foreach (var island in mi.islands)
            {
                LinkTexturesToIsland(state, mi, island, submeshRanges);
            }
        }

        /// <summary>平移归一 / shift-normalization. 返回 false 表示跨缝无法归一.</summary>
        private static bool TryNormalize(ATOMeshInfo mi, ATOIsland island, HashSet<int> verts, List<Vector2> uvList)
        {
            var b = island.uvBounds;
            const float eps = 1e-4f;
            bool oob = b.xMin < -eps || b.yMin < -eps || b.xMax > 1 + eps || b.yMax > 1 + eps;
            if (!oob) return true;

            if (b.width > 1f + eps || b.height > 1f + eps) return false;

            int kx = Mathf.FloorToInt(b.xMin);
            int ky = Mathf.FloorToInt(b.yMin);
            if (b.xMax - kx > 1f + eps || b.yMax - ky > 1f + eps) return false;

            // 顶点被多个岛共享时, 移位可能冲突 -> 放弃归一(保守) / shared vertices may conflict -> give up (conservative)
            foreach (var other in mi.islands)
            {
                if (other.channel != island.channel) continue;
                foreach (var t in other.triangles)
                {
                    int[] tris = mi.mesh.triangles;
                    if (verts.Contains(tris[t * 3]) || verts.Contains(tris[t * 3 + 1]) || verts.Contains(tris[t * 3 + 2]))
                        return false;
                }
            }

            // 写入归一后的UV / write the normalized UVs
            var newUVs = mi.newUVs[island.channel];
            foreach (var v in verts)
            {
                var uv = newUVs[v];
                newUVs[v] = new Vector2(uv.x - kx, uv.y - ky);
            }

            island.uvBounds = new Rect(b.xMin - kx, b.yMin - ky, b.width, b.height);
            island.normalized = true;
            ATOLog.InfoVerbose($"UV岛越界已平移归一 / out-of-bounds island shift-normalized: ({b.xMin:F3},{b.yMin:F3}) size ({b.width:F3},{b.height:F3}) on {mi.renderer.name}");
            return true;
        }

        private static void MarkIslandTexturesWhitelist(ATOBuildState state, ATOMeshInfo mi, ATOIsland island, ATOSkipReason reason, string detail)
        {
            // 此时贴图映射尚未建立, 按子网格区间判断哪些贴图会被该岛使用 / textures are linked later;
            // determine candidates via submesh ranges so only genuinely affected textures are marked
            var submeshRanges = new List<(int start, int end)>();
            for (int s = 0; s < mi.mesh.subMeshCount; s++)
            {
                var sm = mi.mesh.GetSubMesh(s);
                submeshRanges.Add((sm.indexStart / 3, (sm.indexStart + sm.indexCount) / 3));
            }

            int tMin = island.triangles.Min();
            int tMax = island.triangles.Max();

            foreach (var tex in state.textures)
            {
                if (tex.skip == ATOSkip.Full) continue;
                bool applies = tex.refs.Any(r =>
                {
                    if (r.renderer != mi.renderer) return false;
                    if (r.slotIndex < 0 || r.slotIndex >= submeshRanges.Count) return true; // 未知槽 -> 保守 / unknown slot -> conservative
                    var range = submeshRanges[r.slotIndex];
                    return tMax >= range.start && tMin < range.end;
                });
                if (!applies) continue;

                tex.skip = ATOSkip.Full;
                tex.skipReason = reason;
                tex.skipDetail = detail;
                state.skippedFull++;
            }
        }

        private static void LinkTexturesToIsland(ATOBuildState state, ATOMeshInfo mi, ATOIsland island,
            List<(int start, int end)> submeshRanges)
        {
            int tMin = island.triangles.Min();
            int tMax = island.triangles.Max();

            foreach (var tex in state.textures)
            {
                // 只链接到对应UV通道 / link only to the texture's UV channel
                if (tex.uvChannel != island.channel) continue;

                bool applies = false;
                foreach (var r in tex.refs)
                {
                    if (r.renderer != mi.renderer) continue;
                    if (r.slotIndex >= 0)
                    {
                        if (r.slotIndex < submeshRanges.Count)
                        {
                            var range = submeshRanges[r.slotIndex];
                            if (tMax >= range.start && tMin < range.end) { applies = true; break; }
                        }
                        else
                        {
                            // 槽数与子网格数不一致 -> 保守处理 / slot/submesh count mismatch -> conservative
                            applies = true;
                            break;
                        }
                    }
                    else
                    {
                        // 动画贴图属性: 作用于全部槽 / animated texture props apply to all slots
                        applies = true;
                        break;
                    }
                }

                if (!applies) continue;

                // 白名单贴图同样入组(用于传播"同UV跳过图集化"规则) / whitelisted textures join too (for UV-sharing propagation)
                island.textures.Add(tex);
                tex.islands.Add(island);

                island.perTexture[tex] = new ATOIslandTexture
                {
                    texture = tex,
                    pixelRect = ComputePixelRect(tex, island.uvBounds)
                };
            }
        }

        private static Rect ComputePixelRect(ATOTextureInfo tex, Rect uvBounds)
        {
            float x0 = Mathf.Clamp(uvBounds.xMin * tex.width - 1f, 0, tex.width - 1);
            float y0 = Mathf.Clamp(uvBounds.yMin * tex.height - 1f, 0, tex.height - 1);
            float x1 = Mathf.Clamp(uvBounds.xMax * tex.width + 1f, x0 + 1, tex.width);
            float y1 = Mathf.Clamp(uvBounds.yMax * tex.height + 1f, y0 + 1, tex.height);
            return new Rect(x0, y0, x1 - x0, y1 - y0);
        }

        // ------------------------------------------------------------------
        private static void MergeOverlappingIslands(ATOBuildState state)
        {
            foreach (var tex in state.textures)
            {
                var islands = tex.islands;
                if (islands.Count < 2) continue;

                // 4px 粒度覆盖栅格 / 4px-granularity coverage grid
                int gw = Mathf.Max(1, Mathf.CeilToInt(tex.width / 4f));
                int gh = Mathf.Max(1, Mathf.CeilToInt(tex.height / 4f));
                var grid = new int[gw * gh];

                var parent = new int[islands.Count];
                for (int i = 0; i < parent.Length; i++) parent[i] = i;

                for (int i = 0; i < islands.Count; i++)
                {
                    var island = islands[i];
                    RasterizeIslandCells(island, tex, gw, gh, (cx, cy) =>
                    {
                        int idx = cy * gw + cx;
                        if (grid[idx] != 0 && grid[idx] != i + 1)
                        {
                            Union(parent, i, grid[idx] - 1);
                        }
                        else
                        {
                            grid[idx] = i + 1;
                        }
                    });
                }

                var merged = new Dictionary<int, List<ATOIsland>>();
                for (int i = 0; i < islands.Count; i++)
                {
                    int root = Find(parent, i);
                    if (!merged.TryGetValue(root, out var list)) merged[root] = list = new List<ATOIsland>();
                    list.Add(islands[i]);
                }

                foreach (var kv in merged)
                {
                    if (kv.Value.Count <= 1) continue;
                    var main = kv.Value[0];
                    ATOLog.InfoVerbose($"同贴图内 {kv.Value.Count} 个重叠岛合并 / merging {kv.Value.Count} overlapping islands on {tex.source.name}");

                    foreach (var other in kv.Value.Skip(1))
                    {
                        main.triangles = main.triangles.Concat(other.triangles).ToArray();
                        var b = main.uvBounds;
                        main.uvBounds = Rect.MinMaxRect(Mathf.Min(b.xMin, other.uvBounds.xMin), Mathf.Min(b.yMin, other.uvBounds.yMin),
                            Mathf.Max(b.xMax, other.uvBounds.xMax), Mathf.Max(b.yMax, other.uvBounds.yMax));
                        main.vertexCount += other.vertexCount;
                        foreach (var t in other.textures)
                        {
                            if (!main.textures.Contains(t)) main.textures.Add(t);
                            if (main.perTexture.TryGetValue(t, out var mt) && other.perTexture.TryGetValue(t, out var ot))
                            {
                                var r = mt.pixelRect;
                                mt.pixelRect = Rect.MinMaxRect(Mathf.Min(r.xMin, ot.pixelRect.xMin), Mathf.Min(r.yMin, ot.pixelRect.yMin),
                                    Mathf.Max(r.xMax, ot.pixelRect.xMax), Mathf.Max(r.yMax, ot.pixelRect.yMax));
                            }
                            else if (other.perTexture.TryGetValue(t, out var ot2))
                            {
                                main.perTexture[t] = ot2;
                            }
                        }

                        other.owner.islands.Remove(other);
                        foreach (var t in other.textures) t.islands.Remove(other);
                    }
                }
            }
        }

        private delegate void CellCallback(int cx, int cy);

        private static void RasterizeIslandCells(ATOIsland island, ATOTextureInfo tex, int gw, int gh, CellCallback cb)
        {
            var uvList = island.owner.newUVs[island.channel];
            int[] tris = island.owner.mesh.triangles;
            foreach (var t in island.triangles)
            {
                Vector2 a = uvList[tris[t * 3]];
                Vector2 b = uvList[tris[t * 3 + 1]];
                Vector2 c = uvList[tris[t * 3 + 2]];

                Vector2 min = Vector2.Min(a, Vector2.Min(b, c));
                Vector2 max = Vector2.Max(a, Vector2.Max(b, c));
                int x0 = Mathf.Clamp(Mathf.FloorToInt(min.x * tex.width / 4f), 0, gw - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt(max.x * tex.width / 4f), x0, gw - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(min.y * tex.height / 4f), 0, gh - 1);
                int y1 = Mathf.Clamp(Mathf.CeilToInt(max.y * tex.height / 4f), y0, gh - 1);

                for (int cy = y0; cy <= y1; cy++)
                {
                    for (int cx = x0; cx <= x1; cx++)
                    {
                        // 格心 + 4角 保守测试 / conservative: cell center + 4 corners
                        if (PointInTriangleUV(new Vector2((cx + 0.5f) * 4f / tex.width, (cy + 0.5f) * 4f / tex.height), a, b, c)
                            || PointInTriangleUV(new Vector2(cx * 4f / tex.width, cy * 4f / tex.height), a, b, c)
                            || PointInTriangleUV(new Vector2((cx + 1) * 4f / tex.width, cy * 4f / tex.height), a, b, c)
                            || PointInTriangleUV(new Vector2(cx * 4f / tex.width, (cy + 1) * 4f / tex.height), a, b, c)
                            || PointInTriangleUV(new Vector2((cx + 1) * 4f / tex.width, (cy + 1) * 4f / tex.height), a, b, c))
                        {
                            cb(cx, cy);
                            break;
                        }
                    }
                }
            }
        }

        private static bool PointInTriangleUV(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sign(p, a, b), d2 = Sign(p, b, c), d3 = Sign(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
        {
            return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
        }

        // ------------------------------------------------------------------
        private static void ComputeAreas(ATOMeshInfo mi)
        {
            var mesh = mi.mesh;
            var basePos = mesh.vertices;
            int[] tris = mesh.triangles;

            // 基础面积与各形态键面积 / base area and per-blendshape areas
            float meshBaseArea = ComputeMeshArea(basePos, tris);
            float maxArea = meshBaseArea;
            if (mi.blendShapeDeltas != null)
            {
                var pos = new Vector3[basePos.Length];
                foreach (var shape in mi.blendShapeDeltas)
                {
                    Array.Copy(basePos, pos, basePos.Length);
                    for (int v = 0; v < pos.Length && v < shape.delta.Length; v++)
                    {
                        pos[v] += shape.delta[v] * 100f; // 权重100 / weight 100
                    }

                    maxArea = Mathf.Max(maxArea, ComputeMeshArea(pos, tris));
                }
            }

            // 世界缩放 / world scale
            var ls = mi.renderer.transform.lossyScale;
            float avgPairProduct = (Mathf.Abs(ls.x * ls.y) + Mathf.Abs(ls.y * ls.z) + Mathf.Abs(ls.z * ls.x)) / 3f;
            float worldScale = avgPairProduct * mi.animatedScaleFactor;
            float shapeFactor = meshBaseArea > 1e-9f ? maxArea / meshBaseArea : 1f;

            foreach (var island in mi.islands)
            {
                // 岛内面积 / per-island area
                double ia = 0;
                foreach (var t in island.triangles)
                {
                    var p0 = basePos[tris[t * 3]];
                    var p1 = basePos[tris[t * 3 + 1]];
                    var p2 = basePos[tris[t * 3 + 2]];
                    ia += Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5;
                }

                // 形态键影响按整体比例近似(保守: 可能放大其他岛, 防发糊) / blendshape effect approximated by the global ratio (conservative: may inflate other islands, anti-blur)
                island.worldArea = (float)ia * worldScale * shapeFactor;
            }
        }

        private static float ComputeMeshArea(Vector3[] pos, int[] tris)
        {
            double area = 0;
            for (int t = 0; t < tris.Length / 3; t++)
            {
                var p0 = pos[tris[t * 3]];
                var p1 = pos[tris[t * 3 + 1]];
                var p2 = pos[tris[t * 3 + 2]];
                area += Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5;
            }

            return (float)area;
        }

        // ------------------------------------------------------------------
        private static void PropagateWhitelistSharing(ATOBuildState state)
        {
            foreach (var mi in state.meshes)
            {
                foreach (var island in mi.islands)
                {
                    bool hasWhitelisted = island.textures.Any(t => t.skip == ATOSkip.Full);
                    if (!hasWhitelisted) continue;

                    foreach (var t in island.textures)
                    {
                        if (t.skip == ATOSkip.Full) continue;
                        t.skip = ATOSkip.AtlasOnly;
                        t.skipReason = ATOSkipReason.WhitelistSharedUV;
                        t.skipDetail = "shares UV with whitelisted texture";
                        state.skippedAtlasOnly++;
                    }

                    island.atlasCandidate = false;
                }
            }
        }

        // ------------------------------------------------------------------
        private static void DetectAlpha(ATOBuildState state)
        {
            foreach (var tex in state.textures)
            {
                if (tex.skip == ATOSkip.Full) continue;
                var readable = ATOTextureIO.EnsureReadable(tex);
                if (readable == null) continue;

                bool hasAlpha = false;
                try
                {
                    if (readable.format == TextureFormat.RGBA32)
                    {
                        var raw = readable.GetRawTextureData<byte>();
                        int step = Mathf.Max(4, (raw.Length / 65536 / 4) * 4);
                        for (int i = 3; i < raw.Length && !hasAlpha; i += step)
                        {
                            if (raw[i] != 255) hasAlpha = true;
                        }
                    }
                    else if (readable.format == TextureFormat.ARGB32)
                    {
                        var raw = readable.GetRawTextureData<byte>();
                        int step = Mathf.Max(4, (raw.Length / 65536 / 4) * 4);
                        for (int i = 0; i < raw.Length && !hasAlpha; i += step)
                        {
                            if (raw[i] != 255) hasAlpha = true;
                        }
                    }
                    else
                    {
                        int w = readable.width, h = readable.height;
                        int stride = Mathf.Max(1, (w * h) / 16384);
                        for (int i = 0; i < w * h && !hasAlpha; i += stride)
                        {
                            if (readable.GetPixel(i % w, i / w).a < 0.999f) hasAlpha = true;
                        }
                    }
                }
                catch (Exception e)
                {
                    ATOLog.Warn($"alpha 检测失败 / alpha detection failed for {tex.source.name}: {e.Message}");
                }

                tex.hasAlpha = hasAlpha;
                if (hasAlpha) ATOLog.InfoVerbose($"贴图含alpha通道 / texture has alpha: {tex.source.name}");

                // 检测完立即释放, 控制内存峰值 / release right away to bound peak memory
                ATOTextureIO.ReleaseReadable(tex);
            }
        }

        // ------------------------------------------------------------------
        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[ra] = rb;
        }
    }
}
