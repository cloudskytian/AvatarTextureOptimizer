// Stage6_Remap — mesh UV rewrite into atlas rects (safety-latched) / 网格 UV 改写进图集矩形（安全锁存）
// One combined rewrite per mesh. Vertex splits share via (carrier, newUV) buckets; normals/tangents/
// boneWeights/blendshapes are copied per duplicated vertex (tangents are NEVER recomputed — spec).
// Safety lattice: a slot is skipped when its islands' atlas rects disagree across atlases (normalized)
// or AAO declares the channel in use; a (texture, class) whose referencing slot is skipped must NOT
// be retargeted, and any slot referencing a blocked (texture, class) is skipped in turn — fixpoint.
// Skipped slots keep original UVs AND original texture references, so they stay consistent.<br>
// 每网格一次性重写。顶点拆分按 (承载者,新UV) 桶共享；法线/切线/骨骼权重/形态键逐副本拷贝（切线绝不重算——需求）。
// 安全锁存：岛跨图集归一化矩形不一致 → 槽跳过；AAO 声明占用通道 → 槽跳过；被跳过槽引用的
// (贴图,类型) 进入 blocked 集合、引用 blocked 的槽反过来也跳过，迭代至不动点。被跳过槽的 UV 与
// 贴图引用同时保持原状，因此始终自洽。
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.ATO.Editor
{
    internal static class Stage6_Remap
    {
        /// <summary>Per-island atlas target (normalized rect + rotation). / 单岛的图集目标（归一化矩形+旋转）。</summary>
        private struct IslandTarget
        {
            internal Island island;
            internal Rect atlasRect;      // normalized [0,1] on the atlas plane / 图集平面上的归一化矩形
            internal bool rotated;
        }

        private sealed class MeshWork
        {
            internal Renderer renderer;
            internal Mesh mesh;
            internal bool isSmr;
            internal readonly List<(UVSlotKey slot, IslandTarget target)> targets = new List<(UVSlotKey, IslandTarget)>();
        }

        internal static void Run(BuildContext ctx, ATOPipeContext pipe, StageProgress progress)
        {
            if (!pipe.settings.generateAtlas) { ATOLog.V("atlas off → no UV remap needed"); return; }

            // ---- island → atlas entries (one island may appear in several alias atlases) / 岛→图集条目 ----
            var entriesOf = new Dictionary<Island, List<(AtlasDef atlas, AtlasDef.Entry entry)>>();
            foreach (var atlas in pipe.atlases)
                foreach (var e in atlas.entries)
                {
                    if (!entriesOf.TryGetValue(e.island, out var list)) entriesOf[e.island] = list = new List<(AtlasDef, AtlasDef.Entry)>();
                    list.Add((atlas, e));
                }

            // ---- per-slot planning + consistency checks / 逐槽规划与一致性检查 ----
            var planned = new Dictionary<UVSlotKey, List<IslandTarget>>();
            foreach (var kv in pipe.slotIslands)
            {
                var slot = kv.Key;
                var list = new List<IslandTarget>();
                bool bad = false;

                // AAO declares this non-zero channel in use → never touch it (safe fallback) / AAO 占用非零通道→不动
                if (slot.renderer is SkinnedMeshRenderer smrC && slot.channel != 0 && AAOCompat.IsTexCoordUsed(smrC, slot.channel))
                {
                    MarkSkip(pipe, slot, ATOL10n.T("ato.warn.aao_channel_inuse", slot.renderer.name, slot.channel));
                    continue;
                }

                foreach (var isl in kv.Value)
                {
                    if (!entriesOf.TryGetValue(isl, out var entries) || entries.Count == 0)
                        continue; // island not atlased (whitelist/whole path) → keep original UVs / 未图集岛保持原UV

                    // normalized rects of every appearance must agree (co-location across atlases) / 跨图集共位一致性
                    Rect first = Normal(entries[0].entry.rect, entries[0].atlas);
                    bool rot = entries[0].entry.rotated;
                    foreach (var (atlas, entry) in entries)
                    {
                        var r = Normal(entry.rect, atlas);
                        if (entry.rotated != rot || !Near(first, r, atlas))
                        {
                            bad = true;
                            break;
                        }
                    }
                    if (bad)
                    {
                        MarkSkip(pipe, slot, ATOL10n.T("ato.warn.uv_atlas_inconsistent",
                            slot.renderer != null ? slot.renderer.name : "?", slot.submesh, slot.channel));
                        break;
                    }
                    list.Add(new IslandTarget { island = isl, atlasRect = first, rotated = rot });
                }
                if (!bad && list.Count > 0) planned[slot] = list;
            }

            // ---- safety fixpoint: skip ⇄ blocked propagation / 安全不动点：跳过⇄替换阻断 传播 ----
            PropagateSkips(pipe);

            // ---- per-mesh rewrite / 逐网格重写 ----
            var works = new Dictionary<Renderer, MeshWork>();
            foreach (var kv in planned)
            {
                var slot = kv.Key;
                if (pipe.skipSlots.Contains(slot)) continue;
                if (slot.renderer == null) continue;
                if (!works.TryGetValue(slot.renderer, out var w))
                {
                    var mesh = slot.renderer is SkinnedMeshRenderer smr ? smr.sharedMesh
                        : (slot.renderer.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null);
                    if (mesh == null) continue;
                    w = new MeshWork { renderer = slot.renderer, mesh = mesh, isSmr = slot.renderer is SkinnedMeshRenderer };
                    works[slot.renderer] = w;
                }
                foreach (var t in kv.Value) w.targets.Add((slot, t));
            }

            int done = 0, remapped = 0;
            foreach (var kv in works)
            {
                done++;
                pipe.CancelCheck(progress, ATOL10n.T("ato.stage.remap"), (float)done / Mathf.Max(1, works.Count));
                try
                {
                    if (ProcessMesh(ctx, pipe, kv.Value)) remapped++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    var msg = $"UV remap failed for '{kv.Key?.name}': {e.Message}; mesh left unchanged.";
                    ATOLog.Warn("[remap fallback] " + msg); pipe.warnings.Add(msg);
                }
            }
            ATOLog.Info(ATOL10n.T("ato.log.remap_done", remapped));
            ATOEvents.Raise("remap", pipe, ctx.AvatarRootObject);
            ATOHookRegistry.Notify("remap", pipe);
        }

        private static Rect Normal(RectInt r, AtlasDef a) =>
            new Rect(r.x / (float)a.width, r.y / (float)a.height, r.width / (float)a.width, r.height / (float)a.height);

        private static bool Near(Rect a, Rect b, AtlasDef atlas)
        {
            float ex = 0.51f / Mathf.Max(1, atlas.width), ey = 0.51f / Mathf.Max(1, atlas.height);
            return Mathf.Abs(a.x - b.x) <= ex && Mathf.Abs(a.width - b.width) <= ex
                && Mathf.Abs(a.y - b.y) <= ey && Mathf.Abs(a.height - b.height) <= ey;
        }

        private static void MarkSkip(ATOPipeContext pipe, UVSlotKey slot, string msg)
        {
            if (pipe.skipSlots.Add(slot)) { ATOLog.Warn(msg); pipe.warnings.Add(msg); }
        }

        /// <summary>Iterate skip ⇄ blocked until fixpoint (spec: unsafe → fallback). / 迭代 跳过⇄阻断 至不动点。</summary>
        private static void PropagateSkips(ATOPipeContext pipe)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var kv in pipe.slotRefs)
                {
                    bool slotSkipped = pipe.skipSlots.Contains(kv.Key);
                    foreach (var r in kv.Value)
                    {
                        foreach (var t in r.textures)
                        {
                            if (!pipe.infoOf.TryGetValue(t, out var info)) continue;
                            var key = (info, r.cls);
                            if (slotSkipped)
                            {
                                if (pipe.blockedTex.Add(key)) { changed = true; ATOLog.V($"blocked ({info.source?.name},{r.cls}) due to skipped slot"); }
                            }
                            else if (pipe.blockedTex.Contains(key))
                            {
                                if (pipe.skipSlots.Add(kv.Key)) { changed = true; ATOLog.V($"slot {kv.Key.renderer?.name}#{kv.Key.submesh}ch{kv.Key.channel} skipped (blocked texture)"); }
                            }
                        }
                    }
                }
            }
        }

        // ---------------------------------------------------------------- mesh rewrite
        private static bool ProcessMesh(BuildContext ctx, ATOPipeContext pipe, MeshWork w)
        {
            var mesh = w.mesh;
            int subCount = mesh.subMeshCount;
            var origVerts = new List<Vector3>(); mesh.GetVertices(origVerts);
            var origNormals = new List<Vector3>(); mesh.GetNormals(origNormals);
            var origTangents = new List<Vector4>(); mesh.GetTangents(origTangents);
            var origColors = new List<Color>(); mesh.GetColors(origColors);
            var origBones = mesh.boneWeights;
            var origBindposes = mesh.bindposes;
            int vc = origVerts.Count;

            var uvLists = new List<Vector4>[8];
            for (int ch = 0; ch < 8; ch++)
            {
                var l = new List<Vector4>(); mesh.GetUVs(ch, l);
                uvLists[ch] = l.Count == vc ? l : null;
            }
            var tris = new List<int>[subCount];
            for (int sm = 0; sm < subCount; sm++) { var t = new List<int>(); mesh.GetTriangles(t, sm); tris[sm] = t; }

            // working copies / 工作副本
            var newVerts = new List<Vector3>(origVerts);
            var newNormals = origNormals.Count == vc ? new List<Vector3>(origNormals) : null;
            var newTangents = origTangents.Count == vc ? new List<Vector4>(origTangents) : null;   // tangents copied, never recomputed / 切线只拷贝
            var newColors = origColors.Count == vc ? new List<Color>(origColors) : null;
            var newBones = origBones != null && origBones.Length == vc ? new List<BoneWeight>(origBones) : null;
            var dupParent = new List<int>(vc);
            for (int i = 0; i < vc; i++) dupParent.Add(i);
            var newUVs = new List<Vector4>[8];
            for (int ch = 0; ch < 8; ch++) newUVs[ch] = uvLists[ch] != null ? new List<Vector4>(uvLists[ch]) : null;

            // per-channel vertex-split state / 各通道的顶点拆分状态
            var dupOwner = new Dictionary<(int ch, int vert), int>();
            var dupShared = new Dictionary<(int carrier, Vector4 uv), int>();
            int splits = 0;

            foreach (var (slot, target) in w.targets)
            {
                int ch = slot.channel;
                if (ch < 0 || ch > 7 || uvLists[ch] == null) continue;
                if (newUVs[ch] == null) newUVs[ch] = new List<Vector4>(uvLists[ch]);
                if (!pipe.slotTriangles.TryGetValue(slot, out var slotTris) || slotTris.Length == 0) continue;
                // map vertex-index triplet (island space) → position inside this slot's tri list / 三元组→槽三角序号
                var triPos = new Dictionary<long, int>();
                for (int i = 0; i < slotTris.Length; i += 3)
                {
                    long k = TriKey(slotTris[i], slotTris[i + 1], slotTris[i + 2]);
                    if (!triPos.ContainsKey(k)) triPos[k] = i;
                }
                var isl = target.island;
                var span = isl.NormalizedSpan;
                var liveTris = tris[slot.submesh];
                var srcUV = uvLists[ch];
                var bucket = new Dictionary<Vector4, int>();

                for (int ti = 0; ti < isl.triIndices.Count; ti += 3)
                {
                    if (!triPos.TryGetValue(TriKey(isl.triIndices[ti], isl.triIndices[ti + 1], isl.triIndices[ti + 2]), out int baseIdx))
                        continue; // defensive: island tri not found in slot tri list / 防御：找不到对应三角形
                    for (int k = 0; k < 3; k++)
                    {
                        int arrIdx = baseIdx + k;
                        if (arrIdx >= liveTris.Count) continue;
                        int v = liveTris[arrIdx];
                        if (v >= srcUV.Count) continue;
                        var uv = srcUV[v];

                        // original [0,1]-normalized island-local coords (integer tile shift applied in Stage2) / 岛局部归一化坐标
                        float lx = Mathf.Clamp01(((uv.x - isl.tileOffset.x) - isl.nMin.x) / Mathf.Max(1e-8f, span.x));
                        float ly = Mathf.Clamp01(((uv.y - isl.tileOffset.y) - isl.nMin.y) / Mathf.Max(1e-8f, span.y));
                        var r = target.atlasRect;
                        Vector4 newUv = target.rotated
                            ? new Vector4(r.x + ly * r.width, r.y + lx * r.height, uv.z, uv.w)   // transpose / 转置
                            : new Vector4(r.x + lx * r.width, r.y + ly * r.height, uv.z, uv.w);

                        if (dupOwner.TryGetValue((ch, v), out int carrier))
                        {
                            if (bucket.TryGetValue(newUv, out int existing)) { liveTris[arrIdx] = existing; continue; }
                            if (dupShared.TryGetValue((carrier, newUv), out int ex2)) { liveTris[arrIdx] = ex2; bucket[newUv] = ex2; continue; }
                            int ni = newVerts.Count;
                            newVerts.Add(newVerts[v]);
                            newNormals?.Add(newNormals[v]);
                            newTangents?.Add(newTangents[v]);
                            newColors?.Add(newColors[v]);
                            newBones?.Add(newBones[v]);
                            for (int c2 = 0; c2 < 8; c2++) newUVs[c2]?.Add(newUVs[c2][v]);
                            dupParent.Add(dupParent[v]);
                            newUVs[ch][ni] = newUv;
                            bucket[newUv] = ni; dupShared[(carrier, newUv)] = ni; splits++;
                            liveTris[arrIdx] = ni;
                        }
                        else
                        {
                            dupOwner[(ch, v)] = v;
                            newUVs[ch][v] = newUv;
                            bucket[newUv] = v;
                        }
                    }
                }
            }

            // ---- assemble the new mesh / 组装新网格 ----
            var nm = new Mesh { name = mesh.name + "_ATO" };
            nm.indexFormat = newVerts.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : mesh.indexFormat;
            nm.SetVertices(newVerts);
            if (newNormals != null && newNormals.Count == newVerts.Count) nm.SetNormals(newNormals);
            if (newTangents != null && newTangents.Count == newVerts.Count) nm.SetTangents(newTangents);
            if (newColors != null && newColors.Count == newVerts.Count) nm.SetColors(newColors);
            if (newBones != null && newBones.Count == newVerts.Count) nm.boneWeights = newBones.ToArray();
            if (origBindposes != null && origBindposes.Length > 0 && newBones != null) nm.bindposes = origBindposes;
            for (int ch = 0; ch < 8; ch++) if (newUVs[ch] != null) nm.SetUVs(ch, newUVs[ch]);
            nm.subMeshCount = subCount;
            for (int sm = 0; sm < subCount; sm++) nm.SetTriangles(tris[sm], sm, false);
            CopyBlendShapes(mesh, nm, vc, newVerts.Count, dupParent);
            nm.RecalculateBounds(); // bounds only — normals/tangents stay original / 仅包围盒——法线切线保持原值

            if (w.isSmr) ((SkinnedMeshRenderer)w.renderer).sharedMesh = nm;
            else if (w.renderer.TryGetComponent<MeshFilter>(out var mfOut)) mfOut.sharedMesh = nm;
            pipe.meshReplacements[mesh] = nm;
            ObjectRegistry.RegisterReplacedObject(mesh, nm);
            ctx.AssetSaver?.SaveAsset(nm);
            ATOLog.V($"mesh '{mesh.name}': {w.targets.Count} island targets, {splits} vertex splits");
            return true;
        }

        private static long TriKey(int a, int b, int c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            return ((long)a << 42) | ((long)b << 21) | (long)c;
        }

        /// <summary>Blendshapes copied per vertex incl. duplicated ones (via parent map). / 形态键逐顶点拷贝（拆分顶点沿父映射）。</summary>
        private static void CopyBlendShapes(Mesh src, Mesh dst, int origCount, int newCount, List<int> dupParent)
        {
            for (int bi = 0; bi < src.blendShapeCount; bi++)
            {
                string bname = src.GetBlendShapeName(bi);
                int frames = src.GetBlendShapeFrameCount(bi);
                for (int f = 0; f < frames; f++)
                {
                    float wgt = src.GetBlendShapeFrameWeight(bi, f);
                    var dv = new Vector3[origCount]; var dn = new Vector3[origCount]; var dt = new Vector3[origCount];
                    src.GetBlendShapeFrameVertices(bi, f, dv, dn, dt);
                    var dv2 = new Vector3[newCount]; var dn2 = new Vector3[newCount]; var dt2 = new Vector3[newCount];
                    for (int vi = 0; vi < newCount; vi++)
                    {
                        int par = dupParent[vi];
                        dv2[vi] = dv[par]; dn2[vi] = dn[par]; dt2[vi] = dt[par];
                    }
                    dst.AddBlendShapeFrame(bname, wgt, dv2, dn2, dt2);
                }
            }
        }
    }
}
