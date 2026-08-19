using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Fosa.AvatarTextureOptimizer.Editor.Islands;

namespace Fosa.AvatarTextureOptimizer.Editor.Apply
{
    // 网格应用器：克隆网格、按装箱结果重写 UV（含越界归一平移与 90° 旋转）、替换渲染器网格引用、
    // 与 AAO 的 UVUsageCompabilityAPI 协同（被 AAO 使用的通道先备份到空闲通道并注册）。
    // Mesh applier: clones meshes, rewrites UVs per packing results (incl. normalization translation & 90° rotation),
    // replaces renderer mesh references, cooperates with AAO's UVUsageCompabilityAPI.
    internal static class MeshApplier
    {
        public static void Apply(ATOContext ctx, ATOReport.Stage stage)
        {
            var planById = new Dictionary<int, Packing.AtlasPlan>();
            foreach (var plan in ctx.atlasPlans) planById[plan.id] = plan;

            // 需要重写的（网格, 通道）→ 岛。Channels needing rewrite → islands.
            var changed = new Dictionary<KeyValuePair<Mesh, int>, List<IslandEntity>>();
            foreach (var e in ctx.islandEntities)
            {
                if (e.atlasId < 0 || e.noAtlasFallback || e.whitelistedFull) continue;
                var key = new KeyValuePair<Mesh, int>(e.mesh, e.uvChannel);
                List<IslandEntity> list;
                if (!changed.TryGetValue(key, out list))
                {
                    list = new List<IslandEntity>();
                    changed[key] = list;
                }
                list.Add(e);
            }
            if (changed.Count == 0) return;

            // 锚定贴图分辨率缓存（岛 → 锚定贴图）。Anchor texture resolution per island.
            foreach (var kv in changed)
            {
                ctx.CheckCancelled();
                var oldMesh = kv.Key.Key;
                Mesh newMesh;
                if (!ctx.meshReplacements.TryGetValue(oldMesh, out newMesh))
                {
                    newMesh = Object.Instantiate(oldMesh);
                    newMesh.name = oldMesh.name + "_ATO";
                    ctx.ndmf.ObjectRegistry.RegisterReplacedObject(oldMesh, newMesh);
                    ctx.meshReplacements[oldMesh] = newMesh;
                }

                // 渲染器引用替换。Replace renderer references.
                foreach (var r in ctx.renderers)
                {
                    if (r is SkinnedMeshRenderer sr && sr.sharedMesh == oldMesh) sr.sharedMesh = newMesh;
                    else if (r is MeshRenderer mr)
                    {
                        var mf = mr.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh == oldMesh) mf.sharedMesh = newMesh;
                    }
                }

                var channel = kv.Key.Value;
                var uvs = new List<Vector2>();
                newMesh.GetUVs(channel, uvs);
                while (uvs.Count < newMesh.vertexCount) uvs.Add(Vector2.zero);

                foreach (var e in kv.Value)
                {
                    if (!planById.TryGetValue(e.atlasId, out var plan)) continue;
                    var anchor = AnchorTexture(e);
                    if (anchor == null) continue;
                    int tw = anchor.width, th = anchor.height;
                    var span = new Vector2(e.uvMax.x - e.uvMin.x, e.uvMax.y - e.uvMin.y);
                    var scale = new Vector2(e.scaleX, e.scaleY);
                    var texSize = new Vector2Int(tw, th);
                    var atlasSize = new Vector2Int(plan.width, plan.height);
                    foreach (var vi in e.vertices)
                    {
                        if (vi < 0 || vi >= uvs.Count) continue;
                        var uv = uvs[vi];
                        var local = uv + e.translation - e.uvMin;
                        uvs[vi] = IslandTransform.MapToAtlasUv(local, span, scale, texSize,
                            e.rectPosPx, atlasSize, e.paddingPx, e.rotation);
                    }
                }
                newMesh.SetUVs(channel, uvs);
                newMesh.UploadMeshData(false);

                // AAO 协同：备份 AAO 使用的通道。AAO cooperation: evacuate channels AAO uses.
                EvacuateForAAO(ctx, kv.Key.Key, newMesh, channel, kv.Value, stage);
            }

            stage.AddLine(string.Format(ATOLocalization.Tr("log.meshApply"), changed.Count));
        }

        // 锚定贴图：岛的第一个非白名单贴图。Anchor texture: the island's first non-whitelisted texture.
        private static Analysis.TextureEntry AnchorTexture(IslandEntity e)
        {
            foreach (var u in e.uses)
            {
                if (u.texture != null && u.whitelistLevel != Analysis.ATOWhitelistLevel.Full) return u.texture;
            }
            return null;
        }

        // AAO 通道疏散：IsTexCoordUsed 为真的通道，把原始 UV 备份到空闲通道并注册（AAO 处理完后会删除备份通道）。
        // AAO evacuation: for channels AAO uses, back up the original UVs to a free channel and register (AAO deletes it later).
        private static void EvacuateForAAO(ATOContext ctx, Mesh oldMesh, Mesh newMesh, int channel, List<IslandEntity> islands, ATOReport.Stage stage)
        {
            if (!NDMF.AAOUVUsageAdapter.Available) return;

            foreach (var r in ctx.renderers)
            {
                ctx.CheckCancelled();
                var sr = r as SkinnedMeshRenderer;
                if (sr == null || sr.sharedMesh != newMesh) continue;

                bool? used = NDMF.AAOUVUsageAdapter.IsTexCoordUsed(sr, channel);
                if (used != true) continue;

                // 找空闲通道（7 往下；未被 AAO 使用且网格无数据）。Find a free channel (from 7 down; unused by AAO and empty in the mesh).
                int free = -1;
                for (int c = 7; c >= 0; c--)
                {
                    if (c == channel) continue;
                    if (NDMF.AAOUVUsageAdapter.IsTexCoordUsed(sr, c) == true) continue;
                    var probe = new List<Vector2>();
                    newMesh.GetUVs(c, probe);
                    if (probe.Count > 0) continue;
                    free = c;
                    break;
                }
                if (free < 0)
                {
                    // 无法疏散：AAO 将使用被改写的 UV → 可能破坏其优化。中止烘焙最安全。
                    // No free channel: AAO would use rewritten UVs → may break its optimization. Abort safely.
                    string msg = string.Format(ATOLocalization.Tr("error.noFreeUvChannel"), sr.name, channel);
                    ATOLog.Error(msg);
                    ATOReport.Report(ctx.ndmf.ErrorReport, nadena.dev.ndmf.ErrorSeverity.Error, "error.noFreeUvChannel", sr.name, channel.ToString());
                    throw new ATOAbortException(msg);
                }

                // 备份原始 UV 到空闲通道并注册。Back up original UVs to the free channel and register.
                var original = new List<Vector2>();
                oldMesh.GetUVs(channel, original);
                while (original.Count < newMesh.vertexCount) original.Add(Vector2.zero);
                newMesh.SetUVs(free, original);

                string err;
                if (!NDMF.AAOUVUsageAdapter.TryRegisterTexCoordEvacuation(sr, channel, free, out err))
                {
                    // 注册失败（如 saved 通道被 AAO 使用）：删除备份，报错。Registration failed: remove the backup, report.
                    newMesh.SetUVs(free, new List<Vector2>());
                    string msg = string.Format(ATOLocalization.Tr("error.evacuationFailed"), sr.name, channel, free, err);
                    ATOLog.Error(msg);
                    ATOReport.Report(ctx.ndmf.ErrorReport, nadena.dev.ndmf.ErrorSeverity.Error, "error.evacuationFailed", sr.name, channel.ToString(), free.ToString(), err ?? "");
                    throw new ATOAbortException(msg);
                }
                stage.AddLine(string.Format(ATOLocalization.Tr("log.evacuated"), sr.name, channel, free));
            }
        }
    }
}
