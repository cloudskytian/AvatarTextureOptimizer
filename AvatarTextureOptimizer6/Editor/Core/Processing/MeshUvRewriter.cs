using System;
using System.Collections.Generic;
using NetFosa.AvatarTextureOptimizer.Editor.AAO;
using NetFosa.AvatarTextureOptimizer.Editor.Analysis;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using NetFosa.AvatarTextureOptimizer.Editor.UV;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.Processing
{
    /// <summary>
    /// 网格 UV 重写器：
    /// - 每个 (mesh, channel) 上，把所有已装箱岛的顶点 UV 重映射到图集矩形
    /// - 顶点同时被不同 UV 组（不同图集）使用时 → 相关岛标记失败（整图缩放兜底）
    /// - AAO 兼容：AAO 使用中的通道先撤离原始 UV 到空闲通道并注册
    /// - 多通道 UV 各自独立处理
    /// </summary>
    public sealed class MeshUvRewriter
    {
        private readonly ATOLogger _logger;
        private readonly List<UvGroup> _groups;

        public MeshUvRewriter(List<UvGroup> groups, ATOLogger logger)
        {
            _groups = groups;
            _logger = logger;
        }

        /// <summary>装箱前冲突检测：顶点被不同 UV 组引用 → 标失败。返回被标记的组集合（其贴图转整图缩放）。</summary>
        public HashSet<UvGroup> DetectVertexConflicts()
        {
            var conflicts = new HashSet<UvGroup>();
            var byMeshChannel = new Dictionary<(Mesh, int), List<UvGroup>>();

            foreach (var g in _groups)
            {
                if (g.failed || g.islands == null || g.islands.Count == 0) continue;
                if (!byMeshChannel.TryGetValue((g.mesh, g.uvChannel), out var list))
                {
                    list = new List<UvGroup>();
                    byMeshChannel[(g.mesh, g.uvChannel)] = list;
                }
                list.Add(g);
            }

            foreach (var kv in byMeshChannel)
            {
                var (mesh, channel) = kv.Key;
                var groups = kv.Value;
                var uvs = UvIslandExtractor.GetUvArray(mesh, channel);
                if (uvs == null) continue;

                // 顶点 → (组, 岛)
                var vertexGroup = new Dictionary<int, (UvGroup group, UvIsland island)>();
                bool anyConflict = false;

                foreach (var g in groups)
                {
                    var slotTris = mesh.GetTriangles(g.slotIndex);
                    foreach (var island in g.islands)
                    {
                        if (island.failed) continue;
                        foreach (var idx in island.triangleIndices)
                        {
                            int v = slotTris[idx];
                            if (vertexGroup.TryGetValue(v, out var existing))
                            {
                                if (existing.group != g)
                                {
                                    // 顶点被不同 UV 组使用 → 冲突
                                    if (!island.failed)
                                    {
                                        island.failed = true;
                                        island.failReason = "vertex shared between different UV groups on same channel; cannot remap UVs";
                                    }
                                    conflicts.Add(g);
                                    conflicts.Add(existing.group);
                                    anyConflict = true;
                                }
                            }
                            else
                            {
                                vertexGroup[v] = (g, island);
                            }
                        }
                    }
                }

                if (anyConflict)
                    _logger.Warn($"[ATO] Mesh '{mesh.name}' channel {channel} has vertices shared across UV groups; affected islands fall back to whole-texture scaling.");
            }
            return conflicts;
        }

        /// <summary>执行 UV 重写。evacuateAAO=true 时先做 AAO 撤离。</summary>
        public void Rewrite(bool evacuateAAO)
        {
            var byMeshChannel = new Dictionary<(Mesh, int), List<UvGroup>>();
            foreach (var g in _groups)
            {
                if (g.failed || g.islands == null || g.islands.Count == 0) continue;
                bool anyAssigned = false;
                foreach (var i in g.islands)
                {
                    if (i.layoutAssigned && !i.failed) { anyAssigned = true; break; }
                }
                if (!anyAssigned) continue;
                if (!byMeshChannel.TryGetValue((g.mesh, g.uvChannel), out var list))
                {
                    list = new List<UvGroup>();
                    byMeshChannel[(g.mesh, g.uvChannel)] = list;
                }
                list.Add(g);
            }

            foreach (var kv in byMeshChannel)
            {
                var (mesh, channel) = kv.Key;
                var groups = kv.Value;
                RewriteChannel(mesh, channel, groups, evacuateAAO);
            }
        }

        private void RewriteChannel(Mesh mesh, int channel, List<UvGroup> groups, bool evacuateAAO)
        {
            var uvs = UvIslandExtractor.GetUvArray(mesh, channel);
            if (uvs == null) return;
            var newUvs = new Vector2[uvs.Length];
            Array.Copy(uvs, newUvs, uvs.Length);

            // 渲染器（用于 AAO 撤离，取第一个使用该 mesh+channel 的 SkinnedMeshRenderer）
            SkinnedMeshRenderer skinned = null;
            foreach (var g in groups)
            {
                if (g.renderer is SkinnedMeshRenderer smr) { skinned = smr; break; }
            }

            // AAO 撤离：把原始 UV 拷贝到空闲通道（在改写前）
            if (evacuateAAO && skinned != null && AAO.UVUsageCompat.IsTexCoordUsed(skinned, channel))
            {
                int freeChannel = AAO.UVUsageCompat.FindFreeChannel(skinned, channel);
                if (freeChannel >= 0)
                {
                    mesh.SetUVs(freeChannel, uvs);
                    if (AAO.UVUsageCompat.RegisterEvacuation(skinned, channel, freeChannel))
                    {
                        _logger.Info($"AAO compat: evacuated UV channel {channel} -> {freeChannel} on '{skinned.name}'.");
                    }
                }
                else
                {
                    _logger.Warn($"[ATO] AAO uses UV channel {channel} on '{skinned.name}' but no free channel available; skipping UV rewrite for safety.");
                    return;
                }
            }

            foreach (var g in groups)
            {
                var slotTris = mesh.GetTriangles(g.slotIndex);
                foreach (var island in g.islands)
                {
                    if (island.failed || !island.layoutAssigned) continue;

                    var bounds = island.uvBounds;
                    var off = island.normalizedOffset;
                    var pos = island.atlasPosUV;
                    float rectU = island.atlasRect.width;
                    float rectV = island.atlasRect.height;

                    foreach (var idx in island.triangleIndices)
                    {
                        int v = slotTris[idx];
                        var uv = uvs[v];
                        float lu = (uv.x - off.x - bounds.x);
                        float lv = (uv.y - off.y - bounds.y);

                        float scaleU = island.scaleU;
                        float scaleV = island.scaleV;
                        // 归一化：lu ∈ [0, aabbW]；乘 scaleU 得 rect 内偏移
                        if (island.rotated90)
                        {
                            // 90° 旋转：dst local u = lv, v = 1-lu（与 ATO_Resample 的 _SrcRotate 采样一致）
                            newUvs[v] = new Vector2(
                                pos.x + (lv / Mathf.Max(bounds.height, 1e-6f)) * rectV,
                                pos.y + (1f - (lu / Mathf.Max(bounds.width, 1e-6f))) * rectU);
                        }
                        else
                        {
                            newUvs[v] = new Vector2(
                                pos.x + (lu / Mathf.Max(bounds.width, 1e-6f)) * rectU,
                                pos.y + (lv / Mathf.Max(bounds.height, 1e-6f)) * rectV);
                        }
                    }
                }
            }

            mesh.SetUVs(channel, newUvs);
        }
    }
}
