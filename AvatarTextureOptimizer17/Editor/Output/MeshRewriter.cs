// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Output/MeshRewriter.cs — 网格 UV 重写 / Mesh UV rewriting
//
// 需求:
//  - 将图集化后的新 UV 写回网格（多通道 UV 独立处理）。
//  - 共享网格若同时被"未参与优化"的渲染器使用（被跳过/白名单），则按渲染器复制网格，
//    避免影响这些渲染器的 UV 采样。
//  - 兼容 AAO UVUsageCompabilityAPI（反射调用；未装 AAO 时跳过）。
//  - 保留全部网格数据（顶点/法线/切线/颜色/骨骼权重/混合形状/子网格）。
// ============================================================================
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 网格重写结果 / Mesh rewrite results.
    /// </summary>
    public sealed class MeshRewriteResult
    {
        /// <summary>源网格 → 新网格 / source mesh → new mesh</summary>
        public Dictionary<Mesh, Mesh> meshMap = new Dictionary<Mesh, Mesh>();
        /// <summary>渲染器 → 使用的网格 / renderer → mesh it now uses</summary>
        public Dictionary<Renderer, Mesh> rendererMesh = new Dictionary<Renderer, Mesh>();

        /// <summary>渲染器 → 原始网格（AAO UV 撤离用原始 UV）/ renderer → source mesh (for AAO UV evacuation)</summary>
        public Dictionary<Renderer, Mesh> rendererSourceMesh = new Dictionary<Renderer, Mesh>();
        public int rewrittenCount;
        public string channelsSummary = "";
    }

    /// <summary>
    /// 网格重写器 / Mesh rewriter.
    /// </summary>
    public static class MeshRewriter
    {
        /// <summary>
        /// 执行全部网格重写 / Rewrite all meshes.
        /// </summary>
        public static MeshRewriteResult Rewrite(AvatarAnalysis analysis, GameObject root)
        {
            var result = new MeshRewriteResult();
            var channelsByMesh = new Dictionary<Mesh, HashSet<int>>();

            // 收集每个网格需要重写的通道（组已装箱） /
            // collect channels to rewrite per mesh (packed groups only)
            foreach (var group in analysis.allGroups)
            {
                if (group.whitelisted || group.islands == null) continue;
                bool anyPacked = group.islands.Any(i => i.packed);
                if (!anyPacked) continue;

                if (!channelsByMesh.TryGetValue(group.mesh, out var set))
                {
                    set = new HashSet<int>();
                    channelsByMesh[group.mesh] = set;
                }
                set.Add(group.uvChannel);
            }

            // 所有渲染器（含被跳过的）/ all renderers (incl. skipped ones)
            var allRenderers = root.GetComponentsInChildren<Renderer>(true);
            var meshUsage = new Dictionary<Mesh, List<Renderer>>();
            foreach (var r in allRenderers)
            {
                var m = GetMesh(r);
                if (m == null) continue;
                if (!meshUsage.TryGetValue(m, out var list))
                {
                    list = new List<Renderer>();
                    meshUsage[m] = list;
                }
                list.Add(r);
            }

            // 参与分析的渲染器集合（其槽位被记录）/ renderers in the analysis
            var analyzedRenderers = new HashSet<Renderer>();
            foreach (var slot in analysis.slots) analyzedRenderers.Add(slot.renderer);

            var summary = new List<string>();

            foreach (var kv in channelsByMesh)
            {
                var mesh = kv.Key;
                var channels = kv.Value;
                if (!meshUsage.TryGetValue(mesh, out var users)) continue;

                bool sharedSafe = users.All(u => analyzedRenderers.Contains(u));

                if (sharedSafe)
                {
                    var newMesh = RewriteMesh(mesh, channels, analysis);
                    if (newMesh == null) continue;
                    result.meshMap[mesh] = newMesh;
                    foreach (var u in users)
                    {
                        result.rendererMesh[u] = newMesh;
                        result.rendererSourceMesh[u] = mesh;
                        AssignMesh(u, newMesh);
                    }
                    result.rewrittenCount++;
                    summary.Add($"{mesh.name}[{string.Join(",", channels.OrderBy(c => c))}]");
                }
                else
                {
                    // 逐渲染器复制（只处理参与分析的渲染器）/
                    // duplicate per renderer (only for analyzed renderers)
                    foreach (var u in users)
                    {
                        if (!analyzedRenderers.Contains(u)) continue;
                        var newMesh = RewriteMesh(mesh, channels, analysis);
                        if (newMesh == null) continue;
                        result.rendererMesh[u] = newMesh;
                        result.rendererSourceMesh[u] = mesh;
                        AssignMesh(u, newMesh);
                        result.rewrittenCount++;
                    }
                    summary.Add($"{mesh.name}[{string.Join(",", channels.OrderBy(c => c))}](per-renderer)");
                }
            }

            result.channelsSummary = string.Join("; ", summary);
            return result;
        }

        private static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (r is MeshRenderer)
            {
                var mf = r.GetComponent<MeshFilter>();
                return mf != null ? mf.sharedMesh : null;
            }
            return null;
        }

        private static Mesh RewriteMesh(Mesh src, HashSet<int> channels, AvatarAnalysis analysis)
        {
            var mesh = Object.Instantiate(src);
            mesh.name = src.name + " (ATO)";

            var maps = new Dictionary<int, Dictionary<int, Island>>();
            foreach (var ch in channels)
            {
                var group = GetGroup(analysis, src, ch);
                if (group == null || group.islands == null) continue;
                var map = new Dictionary<int, Island>();
                foreach (var island in group.islands)
                {
                    if (!island.packed) continue;
                    foreach (var t in island.triangles)
                    {
                        map[src.triangles[t * 3]] = island;
                        map[src.triangles[t * 3 + 1]] = island;
                        map[src.triangles[t * 3 + 2]] = island;
                    }
                }
                maps[ch] = map;
            }

            foreach (var kv in maps)
            {
                int ch = kv.Key;
                var map = kv.Value;
                var uvs = new List<Vector2>();
                src.GetUVs(ch, uvs);
                if (uvs.Count == 0) continue;

                var group = GetGroup(analysis, src, ch);
                float atlasSize = group != null && group.islands.Count > 0 && group.islands[0].atlas != null
                    ? group.islands[0].atlas.width
                    : 1f;

                for (int i = 0; i < uvs.Count; i++)
                {
                    if (!map.TryGetValue(i, out var island)) continue;

                    var uv = uvs[i] + island.shift; // OOB 归一 / OOB normalization
                    float uw = Mathf.Max(1e-6f, island.uvMax.x - island.uvMin.x);
                    float uh = Mathf.Max(1e-6f, island.uvMax.y - island.uvMin.y);
                    float lu = (uv.x - island.uvMin.x) / uw;
                    float lv = (uv.y - island.uvMin.y) / uh;

                    float rectW = island.rotated ? island.finalH : island.finalW;
                    float rectH = island.rotated ? island.finalW : island.finalH;

                    float nu = lu, nv = lv;
                    if (island.rotated)
                    {
                        // 内容 90° CW 旋转的逆映射: (lu, lv) → (1-lv, lu) /
                        // inverse of the 90° CW content rotation
                        nu = 1f - lv;
                        nv = lu;
                    }

                    float px = island.finalRect.x + nu * rectW;
                    float py = island.finalRect.y + nv * rectH;
                    uvs[i] = new Vector2(px / atlasSize, py / atlasSize);
                }

                mesh.SetUVs(ch, uvs);
            }

            return mesh;
        }

        private static UVGroup GetGroup(AvatarAnalysis analysis, Mesh mesh, int channel)
        {
            if (analysis.groupsByMesh.TryGetValue(mesh, out var map) && map.TryGetValue(channel, out var g))
            {
                return g;
            }
            return null;
        }

        private static void AssignMesh(Renderer r, Mesh mesh)
        {
            if (r is SkinnedMeshRenderer smr)
            {
                smr.sharedMesh = mesh;
            }
            else if (r is MeshRenderer)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = mesh;
            }
        }

        // ---- AAO UVUsageCompabilityAPI（反射）/ AAO UV usage compatibility (reflection) ----

        /// <summary>
        /// 对重写过的 SkinnedMeshRenderer 处理 AAO UV 兼容（撤离原始 UV 到空闲通道）/
        /// Register AAO UV evacuation for rewritten SkinnedMeshRenderers.
        /// </summary>
        public static void ApplyAaoCompatibility(MeshRewriteResult result, AvatarAnalysis analysis)
        {
            var api = AaoReflector.Api;
            if (api == null)
            {
                Log.VerboseLog("AAO not installed; skipping UV compatibility (expected).");
                return;
            }

            foreach (var kv in result.rendererMesh)
            {
                var r = kv.Key;
                if (!(r is SkinnedMeshRenderer smr)) continue;
                var newMesh = kv.Value;
                // 原始 UV 必须来自替换前的源网格（替换后 GetMesh 会拿到新网格）/
                // original UVs must come from the pre-replacement source mesh
                var srcMesh = result.rendererSourceMesh.TryGetValue(r, out var src) ? src : GetMesh(r);
                if (srcMesh == null) continue;

                // 该渲染器被重写过的通道 / channels rewritten for this renderer
                var rewrittenChannels = new HashSet<int>();
                foreach (var ch in Enumerable.Range(0, 8))
                {
                    if (analysis.groupsByMesh.TryGetValue(srcMesh, out var map) &&
                        map.TryGetValue(ch, out var g) && !g.whitelisted && g.islands != null &&
                        g.islands.Any(i => i.packed))
                    {
                        rewrittenChannels.Add(ch);
                    }
                }

                foreach (var ch in rewrittenChannels)
                {
                    if (!api.IsTexCoordUsed(smr, ch)) continue;

                    for (int s = 0; s < 8; s++)
                    {
                        if (rewrittenChannels.Contains(s)) continue;
                        if (api.IsTexCoordUsed(smr, s)) continue;
                        var origUvs = new List<Vector2>();
                        srcMesh.GetUVs(ch, origUvs);
                        newMesh.SetUVs(s, origUvs);
                        try
                        {
                            api.RegisterTexCoordEvacuation(smr, ch, s);
                        }
                        catch (System.Exception e)
                        {
                            Log.Warning($"AAO UV evacuation failed for {smr.name} ch{ch}: {e.Message}");
                        }
                        break;
                    }
                }
            }
        }
    }

}
