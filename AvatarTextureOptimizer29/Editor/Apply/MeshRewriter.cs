// Mesh UV rewriting: per-channel mapped UVs with vertex splitting on context conflicts,
// blendshape frames duplicated across splits, tangents copied verbatim (never recomputed),
// bone weights remapped. Original UVs are kept in the ORIG context for un-atlased submeshes.
// 网格UV重写：按通道映射UV，冲突上下文分裂顶点，形态键随分裂复制，切线原样拷贝绝不重算，
// 骨骼权重重映射。未图集化子网格保留原始UV上下文。

using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato.editor
{
    internal static class MeshRewriter
    {
        /// <summary>Rewrite meshes of all renderers that have atlased islands.
        /// 重写所有含图集化岛的渲染器网格。</summary>
        internal static void Rewrite(AtoSession s)
        {
            using var _ = ATOLog.Scope("RewriteMeshes");

            // islands per (renderer, channel) / 每渲染器每通道的岛
            var byRenderer = new Dictionary<Renderer, List<UvIsland>>();
            foreach (var isl in s.islands)
            foreach (var g in isl.groups)
            {
                if (!byRenderer.TryGetValue(g.ri.renderer, out var list)) byRenderer[g.ri.renderer] = list = new List<UvIsland>();
                if (!list.Contains(isl)) list.Add(isl);
            }

            // page-rect per island (normalized by page) / 岛的页面归一化矩形
            var normRect = new Dictionary<UvIsland, (Rect r, bool rot)>();
            foreach (var atlas in s.atlases)
            foreach (var p in atlas.placements)
                normRect[p.island] = (new Rect(
                    (float)p.rect.xMin / atlas.pageW, (float)p.rect.yMin / atlas.pageH,
                    (float)p.rect.width / atlas.pageW, (float)p.rect.height / atlas.pageH), p.rotated);

            foreach (var ri in s.renderers)
            {
                if (!byRenderer.TryGetValue(ri.renderer, out var islands) || islands.Count == 0) continue;

                // does each slot end up atlased? / 每槽位最终是否图集化
                bool[] slotAtlased = ComputeSlotAtlased(s, ri);
                if (!ArrayTrue(slotAtlased)) continue; // nothing atlased here / 该渲染器无图集化

                // backup original UVs of channels about to be rewritten / 备份将被改写通道的原始UV
                var rewritten = new HashSet<int>();
                foreach (var isl in islands)
                    foreach (var g in isl.groups)
                        if (g.ri == ri && !rewritten.Contains(g.channel))
                        {
                            rewritten.Add(g.channel);
                            var l = new List<Vector2>();
                            ri.mesh.GetUVs(g.channel, l);
                            if (l.Count > 0 && !ri.originalUvBackup.ContainsKey(g.channel))
                                ri.originalUvBackup[g.channel] = l.ToArray();
                        }

                var newMesh = BuildRewritten(s, ri, islands, slotAtlased, normRect);
                if (newMesh == null) continue;
                s.rewrittenChannels[ri.renderer] = rewritten;

                if (ri.skinned) ((SkinnedMeshRenderer)ri.renderer).sharedMesh = newMesh;
                else
                {
                    var mf = ri.renderer.GetComponent<MeshFilter>();
                    if (mf != null) mf.sharedMesh = newMesh;
                }

                ri.mesh = newMesh;
                AAOCompat.EvacuateOriginalUVs(s, ri);
                ATOLog.DebugL($"rewrote mesh for {ri.path}: {newMesh.vertexCount} verts");
            }
        }

        private static bool ArrayTrue(bool[] a)
        {
            foreach (var b in a)
                if (b) return true;
            return false;
        }

        /// <summary>slotAtlased[slot] = any texture replaced by an atlas page on this slot.
        /// 槽位上存在被图集页替换的贴图 = true。</summary>
        private static bool[] ComputeSlotAtlased(AtoSession s, RendererInfo ri)
        {
            // A slot (= submesh) is atlased when any placed island covers one of its
            // triangles. / 槽位(=子网格)存在已装箱岛覆盖其三角形即视为图集化。
            var result = new bool[Mathf.Max(ri.slotMaterials.Count, ri.mesh.subMeshCount)];
            var triSubmesh = new Dictionary<int, int>();
            for (int sm = 0; sm < ri.mesh.subMeshCount; sm++)
                foreach (var t in ri.mesh.GetTriangles(sm))
                    triSubmesh[t / 3] = sm;

            foreach (var atlas in s.atlases)
                foreach (var p in atlas.placements)
                    foreach (var g in p.island.groups)
                        if (g.ri == ri)
                            foreach (var t in g.triangles)
                                if (triSubmesh.TryGetValue(t, out int sm2) && sm2 < result.Length)
                                    result[sm2] = true;
            return result;
        }

        // ------------------------------------------------------------------
        private static Mesh BuildRewritten(AtoSession s, RendererInfo ri, List<UvIsland> islands,
            bool[] slotAtlased, Dictionary<UvIsland, Rect> normRect)
        {
            var src = ri.mesh;
            var tris = src.triangles;
            int vc = src.vertexCount;
            int tc = tris.Length / 3;

            // triangle -> island per channel / 三角形到岛映射
            var triIsland = new Dictionary<int, UvIsland>[4];
            for (int c = 0; c < 4; c++) triIsland[c] = new Dictionary<int, UvIsland>();
            foreach (var isl in islands)
            foreach (var g in isl.groups)
                foreach (var t in g.triangles)
                    if (!triIsland[g.channel].ContainsKey(t))
                        triIsland[g.channel][t] = isl; // first island wins; overlap merged earlier

            // required context per (submesh, triangle) / 每(子网格,三角形)所需上下文
            const int ORIG = -1;
            var subTris = new List<int>[src.subMeshCount];
            for (int sm = 0; sm < src.subMeshCount; sm++)
            {
                var st = src.GetTriangles(sm);
                subTris[sm] = new List<int>(st.Length / 3);
                for (int t = 0; t < st.Length / 3; t++) subTris[sm].Add(t);
            }

            // Contexts per vertex: (channel-independent) island ids + ORIG.
            // Each triangle in submesh sm requires context:
            //   any channel island containing t & slotAtlased[sm]  -> that island (per channel!)
            // Since a triangle may be mapped on multiple channels, context must include channel:
            // context key = island.id * 8 + channel. ORIG = -1.
            // / 上下文键 = 岛id*8+通道；ORIG = -1
            var remap = new Dictionary<(int v, int ctx), int>();
            var newVerts = new List<int>(vc);

            int CtxOf(UvIsland isl, int ch) => isl.id * 8 + ch;

            // vertex -> contexts / 顶点 -> 上下文集合
            var vertCtxs = new Dictionary<int, HashSet<int>>();
            for (int sm = 0; sm < src.subMeshCount && sm < slotAtlased.Length; sm++)
            {
                if (!slotAtlased[sm]) continue;
                foreach (var t in subTris[sm])
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int v = tris[t * 3 + k];
                        if (!vertCtxs.TryGetValue(v, out var set)) vertCtxs[v] = set = new HashSet<int>();
                        for (int ch = 0; ch < 4; ch++)
                            if (triIsland[ch].TryGetValue(t, out var isl))
                                set.Add(CtxOf(isl, ch));
                    }
                }
            }

            // ORIG context for all vertices (un-atlased submeshes always need it)
            // 全部顶点保留 ORIG 上下文（未图集化子网格需要）
            for (int v = 0; v < vc; v++)
            {
                if (!vertCtxs.TryGetValue(v, out var set)) vertCtxs[v] = set = new HashSet<int>();
                set.Add(ORIG);
            }

            // build new vertex order / 新顶点顺序
            var ctxListOf = new Dictionary<int, List<int>>();
            foreach (var kv in vertCtxs)
            {
                var list = new List<int>(kv.Value);
                list.Sort();
                ctxListOf[kv.Key] = list;
                foreach (var ctx in list)
                {
                    remap[(kv.Key, ctx)] = newVerts.Count;
                    newVerts.Add(kv.Key);
                }
            }

            int nvc = newVerts.Count;

            // ---- attributes / 属性 ----
            var pos = src.vertices;
            var nrm = src.normals;
            var tan = src.tangents;
            var col = src.colors32.Length == vc ? src.colors32 : null;
            var uvArr = new Vector2[4][];
            for (int c = 0; c < 4; c++)
            {
                var l = new List<Vector2>();
                src.GetUVs(c, l);
                uvArr[c] = l.Count == vc ? l.ToArray() : null;
            }

            var nPos = new Vector3[nvc];
            var nNrm = nrm.Length == vc ? new Vector3[nvc] : null;
            var nTan = tan.Length == vc ? new Vector4[nvc] : null;
            var nCol = col != null ? new Color32[nvc] : null;
            var nUv = new Vector2[4][];
            for (int c = 0; c < 4; c++) nUv[c] = uvArr[c] != null ? new Vector2[nvc] : null;

            foreach (var kv in ctxListOf)
            {
                int v = kv.Key;
                foreach (var ctx in kv.Value)
                {
                    int idx = remap[(v, ctx)];
                    nPos[idx] = pos[v];
                    if (nNrm != null) nNrm[idx] = nrm[v];
                    if (nTan != null) nTan[idx] = tan[v];
                    if (nCol != null) nCol[idx] = col[v];
                    for (int c = 0; c < 4; c++)
                    {
                        if (nUv[c] == null) continue;
                        Vector2 val = uvArr[c][v];
                        if (ctx >= 0)
                        {
                            int islId = ctx / 8, ch = ctx % 8;
                            if (ch == c)
                            {
                                var isl = islands.Find(i => i.id == islId);
                                if (isl != null && normRect.TryGetValue(isl, out var rr))
                                {
                                    s.uvOffsets.TryGetValue((src, c), out var off);
                                    val = MapUv(val, isl, rr.r, rr.rot, off);
                                }
                            }
                        }
                        nUv[c][idx] = val;
                    }
                }
            }

            // bone weights remap / 骨骼权重重映射
            var bonesPerVertex = new byte[vc];
            var allWeights = src.GetAllBoneWeights(out var bpv);
            bpv.CopyTo(bonesPerVertex, 0);
            var nBpv = new byte[nvc];
            var nWeights = new List<BoneWeight1>(allWeights.Length);
            int cursor = 0;
            var weightStart = new int[vc];
            for (int v = 0; v < vc; v++) weightStart[v] = cursor, cursor += bonesPerVertex[v];
            foreach (var kv in ctxListOf)
            foreach (var ctx in kv.Value)
            {
                int idx = remap[(kv.Key, ctx)];
                nBpv[idx] = bonesPerVertex[kv.Key];
                for (int w = 0; w < bonesPerVertex[kv.Key]; w++)
                    nWeights.Add(allWeights[weightStart[kv.Key] + w]);
            }

            // blendshapes / 形态键
            var shapeData = CaptureBlendshapes(src, vc);

            // ---- new mesh / 新网格 ----
            var m = new Mesh { name = src.name + "(ATO)" };
            m.indexFormat = nvc > 65535 ? IndexFormat.UInt32 : src.indexFormat;
            m.vertices = nPos;
            if (nNrm != null) m.normals = nNrm;
            if (nTan != null) m.tangents = nTan;
            if (nCol != null) m.colors32 = nCol;
            for (int c = 0; c < 4; c++)
                if (nUv[c] != null)
                    m.SetUVs(c, new List<Vector2>(nUv[c]));
            m.bindposes = src.bindposes;
            if (ri.skinned)
                m.SetBoneWeights(nBpv, nWeights);
            ApplyBlendshapes(m, shapeData, ctxListOf, remap, nvc);

            // triangles per submesh with per-triangle context / 按子网格与上下文重建索引
            for (int sm = 0; sm < src.subMeshCount; sm++)
            {
                var st = src.GetTriangles(sm);
                var outTris = new List<int>(st.Length);
                bool atlased = sm < slotAtlased.Length && slotAtlased[sm];
                for (int t = 0; t < st.Length / 3; t++)
                {
                    // context for this submesh: island on channel of this triangle if atlased
                    int ctx = ORIG;
                    if (atlased)
                        for (int ch = 0; ch < 4; ch++)
                            if (triIsland[ch].TryGetValue(t, out var isl))
                            {
                                ctx = CtxOf(isl, ch);
                                break;
                            }

                    for (int k = 0; k < 3; k++)
                        outTris.Add(remap[(st[t * 3 + k], ctx)]);
                }

                m.SetTriangles(outTris, sm);
            }

            m.RecalculateBounds();
            s.ctx.AssetSaver.SaveAsset(m);
            return m;
        }

        /// <summary>UV -> atlas page mapping (normalized, rotation & offset aware).
        /// UV到图集页映射（归一化、含旋转与归一化平移）。</summary>
        internal static Vector2 MapUv(Vector2 uv, UvIsland isl, Rect normRect, bool rotated, Vector2 offset)
        {
            uv -= offset;
            float u = (uv.x - isl.uvBounds.xMin) / Mathf.Max(1e-9f, isl.uvBounds.width);
            float v = (uv.y - isl.uvBounds.yMin) / Mathf.Max(1e-9f, isl.uvBounds.height);
            if (rotated) (u, v) = (v, u); // transpose / 转置
            return new Vector2(
                normRect.x + u * normRect.width,
                normRect.y + v * normRect.height);
        }

        // ------------------------------------------------------------------
        private class BlendshapeCapture
        {
            internal string name;
            internal float frameWeight;
            internal Vector3[] dv, dn, dt;
        }

        private static List<BlendshapeCapture> CaptureBlendshapes(Mesh src, int vc)
        {
            var list = new List<BlendshapeCapture>();
            for (int si = 0; si < src.blendShapeCount; si++)
            {
                int frames = src.GetBlendShapeFrameCount(si);
                for (int f = 0; f < frames; f++)
                {
                    var dv = new Vector3[vc];
                    var dn = new Vector3[vc];
                    var dt = new Vector3[vc];
                    src.GetBlendShapeFrameVertices(si, f, dv, dn, dt);
                    list.Add(new BlendshapeCapture
                    {
                        name = src.GetBlendShapeName(si),
                        frameWeight = src.GetBlendShapeFrameWeight(si, f),
                        dv = dv, dn = dn, dt = dt,
                    });
                }
            }
            return list;
        }

        private static void ApplyBlendshapes(Mesh m, List<BlendshapeCapture> shapes,
            Dictionary<int, List<int>> ctxListOf, Dictionary<(int, int), int> remap, int nvc)
        {
            string current = null;
            foreach (var cap in shapes)
            {
                var dv = new Vector3[nvc];
                var dn = new Vector3[nvc];
                var dt = new Vector3[nvc];
                foreach (var kv in ctxListOf)
                foreach (var ctx in kv.Value)
                {
                    int idx = remap[(kv.Key, ctx)];
                    dv[idx] = cap.dv[kv.Key];
                    dn[idx] = cap.dn[kv.Key];
                    dt[idx] = cap.dt[kv.Key];
                }

                if (cap.name != current)
                {
                    m.AddBlendShapeFrame(cap.name, cap.frameWeight, dv, dn, dt);
                    current = cap.name;
                }
                else
                    m.AddBlendShapeFrame(cap.name, cap.frameWeight, dv, dn, dt);
            }
        }
    }
}
