// Stage2_UV — island extraction, OOB normalization, groups, world area / 岛提取、越界归一、组构建、世界面积
// Islands per (renderer, submesh, channel); integer-tile normalization rejects wrap-seam-crossing UVs
// (those slots' textures are whitelisted with a warning). World area accounts for blendshapes at weight
// 0/100 (max only, no combinations — spec) and animation-driven scale (max).<br>
// CCTV：岛按 (渲染器,子网格,UV通道) 提取；整数平铺归一化拒绝跨缝 UV（对应贴图白名单+警告）；
// 世界面积考虑形态键 0/100 最大值（不组合）与动画缩放最大值。
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    internal static class Stage2_UV
    {
        internal static void Run(BuildContext ctx, ATOPipeContext pipe, StageProgress progress)
        {
            // ---------- island extraction per slot / 逐槽岛提取 ----------
            int si = 0; var slots = new List<UVSlotKey>(pipe.slotRefs.Keys);
            foreach (var slot in slots)
            {
                si++;
                if ((si & 1) == 0) pipe.CancelCheck(progress, ATOL10n.T("ato.stage.uv"), (float)si / slots.Count * 0.6f);

                var refs = pipe.slotRefs[slot];
                if (refs.Count == 0) continue;
                var mesh = slot.renderer is SkinnedMeshRenderer smr ? smr.sharedMesh
                    : (slot.renderer.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null);
                if (mesh == null || slot.submesh >= mesh.subMeshCount) continue;
                if (slot.channel < 0 || slot.channel > 7 || !mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0 + slot.channel))
                {
                    ATOLog.Warn(ATOL10n.T("ato.warn.uvchannel_missing", slot.renderer.name, slot.channel));
                    WhitelistSlot(pipe, refs, "uv channel missing / UV通道缺失");
                    continue;
                }

                var tris = mesh.GetTriangles(slot.submesh);
                var uvs = new List<Vector2>();
                mesh.GetUVs(slot.channel, uvs);
                if (uvs.Count != mesh.vertexCount) { WhitelistSlot(pipe, refs, "uv count mismatch"); continue; }
                pipe.slotTriangles[slot] = tris;

                var islands = ExtractIslands(slot, tris, uvs);
                // normalized [0,1] remap; reject wrap-seam crossing / 归一化重映射，拒绝跨缝
                bool wrapped = false;
                foreach (var isl in islands)
                {
                    if (!TryNormalize(isl))
                    {
                        wrapped = true;
                        break;
                    }
                }
                if (wrapped)
                {
                    ATOLog.Warn(ATOL10n.T("ato.warn.uv_wrap", slot.renderer.name, slot.submesh, slot.channel));
                    WhitelistSlot(pipe, refs, "uv crosses wrap seam / UV跨wrap缝");
                    continue;
                }
                MergeOverlapping(islands);
                pipe.slotIslands[slot] = islands;
            }
            pipe.CancelCheck(progress, ATOL10n.T("ato.stage.uv"), 0.65f);

            // ---------- packing groups via texture connectivity / 贴图连通构建超组 ----------
            BuildGroups(pipe, progress);
            pipe.CancelCheck(progress, ATOL10n.T("ato.stage.uv"), 0.8f);

            // ---------- world areas / 世界面积 ----------
            ComputeWorldAreas(pipe, progress);
            ATOEvents.Raise("uv", pipe, ctx.AvatarRootObject);
            ATOHookRegistry.Notify("uv", pipe);
        }

        // ---------------------------------------------------------------- islands
        /// <summary>Segment triangles into UV islands (weld by quantized UV). / 以量化UV焊接法切分UV岛。</summary>
        private static List<Island> ExtractIslands(UVSlotKey slot, int[] tris, List<Vector2> uvs)
        {
            int triCount = tris.Length / 3;
            var parent = new int[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

            // weld UV points by quantization / 量化焊接
            var weld = new Dictionary<long, int>();
            for (int t = 0; t < triCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    var uv = uvs[tris[3 * t + k]];
                    long key = WeldKey(uv);
                    if (weld.TryGetValue(key, out int other)) Union(t, other);
                    else weld[key] = t;
                }
            }

            var byRoot = new Dictionary<int, Island>();
            for (int t = 0; t < triCount; t++)
            {
                int rootT = Find(t);
                if (!byRoot.TryGetValue(rootT, out var isl)) byRoot[rootT] = isl = new Island { slot = slot };
                isl.triIndices.Add(tris[3 * t]); isl.triIndices.Add(tris[3 * t + 1]); isl.triIndices.Add(tris[3 * t + 2]);
                for (int k = 0; k < 3; k++)
                {
                    var uv = uvs[tris[3 * t + k]];
                    if (isl.triIndices.Count == 3 && k == 0) { isl.uvMin = isl.uvMax = uv; }
                    isl.uvMin = Vector2.Min(isl.uvMin, uv);
                    isl.uvMax = Vector2.Max(isl.uvMax, uv);
                }
            }
            return new List<Island>(byRoot.Values);
        }

        private static long WeldKey(Vector2 uv)
        {
            // 1/8192 quantization grid / 1/8192 量化网格
            long x = (long)Math.Round(uv.x * 8192.0);
            long y = (long)Math.Round(uv.y * 8192.0);
            return (x << 32) ^ (y & 0xffffffffL);
        }

        /// <summary>Integer-tile normalization; false if the island crosses a wrap seam. / 整数平铺归一化；跨缝返回 false。</summary>
        private static bool TryNormalize(Island isl)
        {
            const float eps = 1f / 4096f;
            int tx = Mathf.FloorToInt(isl.uvMin.x + 1e-6f);
            int ty = Mathf.FloorToInt(isl.uvMin.y + 1e-6f);
            if (isl.uvMax.x > tx + 1 + eps || isl.uvMax.y > ty + 1 + eps) return false; // crosses seam / 跨缝
            isl.tileOffset = new Vector2Int(tx, ty);
            isl.nMin = new Vector2(Mathf.Clamp01(isl.uvMin.x - tx), Mathf.Clamp01(isl.uvMin.y - ty));
            isl.nMax = new Vector2(Mathf.Clamp01(isl.uvMax.x - tx), Mathf.Clamp01(isl.uvMax.y - ty));
            // guard: degenerate islands get minimal size to avoid NaNs later / 退化岛保底尺寸
            if (isl.nMax.x - isl.nMin.x < eps) isl.nMax.x = Mathf.Min(1f, isl.nMin.x + eps);
            if (isl.nMax.y - isl.nMin.y < eps) isl.nMax.y = Mathf.Min(1f, isl.nMin.y + eps);
            return true;
        }

        /// <summary>Merge self-overlapping islands inside one slot (union bbox). / 合并同槽内自重叠的岛（并集包围盒）。</summary>
        private static void MergeOverlapping(List<Island> islands)
        {
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < islands.Count && !merged; i++)
                for (int j = i + 1; j < islands.Count && !merged; j++)
                {
                    var a = islands[i]; var b = islands[j];
                    if (a.tileOffset != b.tileOffset) continue;
                    if (a.nMax.x < b.nMin.x || b.nMax.x < a.nMin.x || a.nMax.y < b.nMin.y || b.nMax.y < a.nMin.y) continue;
                    a.triIndices.AddRange(b.triIndices);
                    a.nMin = Vector2.Min(a.nMin, b.nMin);
                    a.nMax = Vector2.Max(a.nMax, b.nMax);
                    islands.RemoveAt(j);
                    merged = true;
                }
            }
        }

        // ---------------------------------------------------------------- groups
        private static void BuildGroups(ATOPipeContext pipe, StageProgress progress)
        {
            var parent = new Dictionary<object, object>();
            object Find(object x) { while (!ReferenceEquals(parent[x], x)) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union2(object a, object b)
            {
                if (!parent.ContainsKey(a)) parent[a] = a;
                if (!parent.ContainsKey(b)) parent[b] = b;
                var ra = Find(a); var rb = Find(b);
                if (!ReferenceEquals(ra, rb)) parent[ra] = rb;
            }

            foreach (var slot in pipe.slotIslands.Keys)
            {
                var refs = pipe.slotRefs[slot];
                var infos = new List<TextureInfo>();
                foreach (var r in refs)
                    foreach (var t in r.textures)
                        if (pipe.infoOf.TryGetValue(t, out var info) && !infos.Contains(info)) infos.Add(info);
                foreach (var info in infos) Union2(slot, info);
            }

            var groupOf = new Dictionary<object, PackingGroup>();
            foreach (var slot in pipe.slotIslands.Keys)
            {
                // slot with no resolvable texture was never unioned → no parent entry / 无可解析贴图的槽从未并查，跳过
                if (!parent.ContainsKey(slot)) { ATOLog.V($"slot {slot.renderer?.name}#{slot.submesh}ch{slot.channel} has no texture info; skipped"); continue; }
                var root = Find(slot);
                if (!groupOf.TryGetValue(root, out var g))
                {
                    groupOf[root] = g = new PackingGroup { id = pipe.groups.Count };
                    pipe.groups.Add(g);
                }
                g.slots.Add(slot);
                foreach (var isl in pipe.slotIslands[slot]) { isl.group = g; g.islands.Add(isl); pipe.islands.Add(isl); }
                foreach (var r in pipe.slotRefs[slot]) g.refs.Add(r);
            }

            // group texture sets, type key, strictness / 组贴图集合、类型键、最严参数
            foreach (var g in pipe.groups)
            {
                foreach (var r in g.refs)
                {
                    foreach (var t in r.textures)
                    {
                        if (!pipe.infoOf.TryGetValue(t, out var info)) continue;
                        if (!g.textures.Contains(info)) g.textures.Add(info);
                        info.classes.Add(r.cls);
                        if (!g.texturesByClass.TryGetValue(r.cls, out var set)) g.texturesByClass[r.cls] = set = new HashSet<TextureInfo>();
                        set.Add(info);
                        // per-class original max size (clamps unified island size) / 各类型最大原尺寸（钳制统一尺寸）
                        if (!g.maxSrcByClass.TryGetValue(r.cls, out var ms)) ms = Vector2Int.zero;
                        g.maxSrcByClass[r.cls] = new Vector2Int(Mathf.Max(ms.x, info.width), Mathf.Max(ms.y, info.height));
                        if (info.whitelisted) g.whitelisted = true;
                    }
                    if (r.cls == TexClass.Albedo)
                    {
                        if ((int)r.alphaMode > (int)g.strictestAlpha) g.strictestAlpha = r.alphaMode;
                        if (r.alphaMode == AlphaMode.Cutout) g.strictestCutoff = Mathf.Max(g.strictestCutoff, r.cutoff);
                    }
                }
                int classMask = 0;
                foreach (var c in g.texturesByClass.Keys) classMask |= TypeGroupKey.ClassBit(c);
                bool srgb = false; int filter = 0;
                foreach (var info in g.textures)
                {
                    if (info.classes.Contains(TexClass.Albedo) && info.sRGB) srgb = true;
                    filter = Mathf.Max(filter, (int)info.filterMode);
                }
                g.typeKey = new TypeGroupKey { classMask = classMask, albedoSRGB = srgb, filterBucket = filter };
            }

            ATOLog.Info(ATOL10n.T("ato.log.uv_done", pipe.slotIslands.Count, pipe.islands.Count, pipe.groups.Count));
        }

        private static void WhitelistSlot(ATOPipeContext pipe, List<MaterialTextureRef> refs, string reason)
        {
            foreach (var r in refs)
                foreach (var t in r.textures)
                    if (pipe.infoOf.TryGetValue(t, out var info)) info.MarkWhitelist(reason);
        }

        // ---------------------------------------------------------------- world area
        private static void ComputeWorldAreas(ATOPipeContext pipe, StageProgress progress)
        {
            // Per renderer: base tri areas + per-blendshape(0/100) max areas / 每渲染器：基础面积与形态键0/100最大面积
            var areaCache = new Dictionary<Mesh, float[]>();          // mesh → base tri areas (per mesh-tri index)
            var perRendererTris = new Dictionary<(Mesh, Renderer), int[]>();
            int ri = 0;
            foreach (var slot in pipe.slotIslands.Keys)
            {
                ri++;
                if ((ri & 1) == 0) pipe.CancelCheck(progress, ATOL10n.T("ato.stage.uv"), 0.8f + 0.2f * ri / (pipe.slotIslands.Count + 1));
                var mesh = slot.renderer is SkinnedMeshRenderer smr0 ? smr0.sharedMesh : (slot.renderer.TryGetComponent<MeshFilter>(out var mf0) ? mf0.sharedMesh : null);
                if (mesh == null) continue;
                var key2 = (mesh, slot.renderer);
                if (!perRendererTris.TryGetValue(key2, out var allTris))
                {
                    allTris = mesh.triangles; // full-mesh tri list for area indexing (islands store vert indices)
                    perRendererTris[key2] = allTris;
                    areaCache[key2.mesh] = ComputeMaxAreas(mesh, slot.renderer as SkinnedMeshRenderer);
                }
                var triMax = areaCache[mesh];
                var state = pipe.rendererStates.TryGetValue(slot.renderer, out var st) ? st : new RendererAnimState();
                var s = state.maxAnimScale;
                float animFactor = Mathf.Max(Mathf.Abs(s.x * s.y), Mathf.Max(Mathf.Abs(s.y * s.z), Mathf.Abs(s.x * s.z)));
                if (animFactor <= 1e-8f) animFactor = 1f;

                // Map this mesh's vert-index triplets to mesh-tri index via a triangle lookup (per renderer+mesh) / 三角查找表
                var lookup = BuildMeshTriLookup(mesh);
                float rendererScale = Mathf.Max(slot.renderer.transform.lossyScale.x, Mathf.Max(slot.renderer.transform.lossyScale.y, slot.renderer.transform.lossyScale.z));
                float staticScale2 = rendererScale * rendererScale; // areas scale by s² / 面积按缩放平方

                foreach (var isl in pipe.slotIslands[slot])
                {
                    float area = 0f;
                    for (int k = 0; k < isl.triIndices.Count; k += 3)
                    {
                        if (lookup.TryGetValue(TriKey(isl.triIndices[k], isl.triIndices[k + 1], isl.triIndices[k + 2]), out int meshTri))
                            area += triMax[meshTri];
                    }
                    isl.worldAreaMax = area * animFactor * staticScale2;
                }
            }
        }

        /// <summary>Per mesh-triangle max area across base & each blendshape(weight 100). / 每网格三角形取基础与各形态键(权重100)的最大面积。</summary>
        private static float[] ComputeMaxAreas(Mesh mesh, SkinnedMeshRenderer smr)
        {
            var verts = mesh.vertices;
            var allTris = mesh.triangles;
            var areas = new float[allTris.Length / 3];
            for (int t = 0; t < areas.Length; t++)
                areas[t] = TriArea(verts[allTris[3 * t]], verts[allTris[3 * t + 1]], verts[allTris[3 * t + 2]]);

            int shapeCount = mesh.blendShapeCount;
            if (smr == null || shapeCount == 0) return areas;

            var dv = new Vector3[verts.Length];
            for (int k = 0; k < shapeCount; k++)
            {
                int frameCount = mesh.GetBlendShapeFrameCount(k);
                int bestFrame = -1; float bestWeight = -1;
                for (int f = 0; f < frameCount; f++)
                {
                    float w = mesh.GetBlendShapeFrameWeight(k, f);
                    if (w <= 100.001f && w > bestWeight) { bestWeight = w; bestFrame = f; }
                }
                if (bestFrame < 0 || bestWeight <= 0.001f) continue; // no frame at/below 100 → only base counts / 无≤100帧
                float scale = 100f / bestWeight;
                mesh.GetBlendShapeFrameVertices(k, bestFrame, dv, null, null);
                bool any = false;
                for (int v = 0; v < dv.Length; v++) if (dv[v].sqrMagnitude > 1e-12f) { any = true; break; }
                if (!any) continue;
                for (int t = 0; t < areas.Length; t++)
                {
                    var a = verts[allTris[3 * t]] + dv[allTris[3 * t]] * scale;
                    var b = verts[allTris[3 * t + 1]] + dv[allTris[3 * t + 1]] * scale;
                    var c = verts[allTris[3 * t + 2]] + dv[allTris[3 * t + 2]] * scale;
                    areas[t] = Mathf.Max(areas[t], TriArea(a, b, c)); // max of {0,100} per shape, no combinations / 各形态键0/100取最大，不组合
                }
            }
            return areas;
        }

        private static float TriArea(Vector3 a, Vector3 b, Vector3 c) => Vector3.Cross(b - a, c - a).magnitude * 0.5f;

        // Deterministic triangle lookup (verts sorted) / 确定性三角形查找（顶点排序）
        private static long TriKey(int a, int b, int c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            return ((long)a << 42) | ((long)b << 21) | (long)c;
        }

        private static Dictionary<long, int> BuildMeshTriLookup(Mesh mesh)
        {
            var allTris = mesh.triangles;
            var dict = new Dictionary<long, int>(allTris.Length / 3);
            for (int t = 0; t < allTris.Length / 3; t++)
            {
                var k = TriKey(allTris[3 * t], allTris[3 * t + 1], allTris[3 * t + 2]);
                if (!dict.ContainsKey(k)) dict[k] = t;
            }
            return dict;
        }
    }
}
