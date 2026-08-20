// ATOMeshWriter.cs — 网格 UV 写入器 / Mesh UV writer.
// 说明：克隆被修改的网格（通过 ObjectRegistry 注册替换，保持形态键等数据），按装箱布局重写 UV：
//  - 顶点 UV 变换：uv' = R( (uv+平移-包围盒)/跨度 × 岛矩形尺寸 ) + 布局位置（归一化）
//  - 仅修改有图集布局的岛；白名单/整图路径的岛 UV 不变
//  - AAO 兼容：若 AAO 可能使用该通道（UVUsageCompabilityAPI），先把原 UV 保存到空闲通道并注册迁移
// Note: clones modified meshes (replacement registered via ObjectRegistry, keeping blendshapes etc.) and rewrites
// UVs per the packing layout: uv' = R((uv+translation-bbox)/span × rect) + placement (normalized).
// Only islands with atlas placements are rewritten; whitelisted / whole-texture islands keep their UVs.
// AAO compat: when AAO may use a channel, the original UVs are saved into a free channel and evacuation is registered.

using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>网格 UV 写入器。/ Mesh UV writer.</summary>
    internal static class ATOMeshWriter
    {
        /// <summary>
        /// 写入全部网格的 UV 变更。evacuationMap: 渲染器 → (原通道 → 保存通道)，用于 AAO 兼容的 UV 迁移。
        /// Write UV changes for all meshes. evacuationMap: renderer → (original channel → saved channel) for AAO-compatible UV evacuation.
        /// </summary>
        public static void Write(BuildContext context, List<ATOTypeGroup> groups, List<ATORendererInfo> renderers,
            Dictionary<Renderer, Dictionary<int, int>> evacuationMap)
        {
            // 收集需要改写的 (mesh, channel) → 岛集合 / collect (mesh, channel) → islands to rewrite
            var meshChannels = new Dictionary<(Mesh, int), List<ATOIsland>>();
            foreach (var group in groups)
            {
                foreach (var kv in group.layout)
                {
                    var island = kv.Key;
                    var placement = kv.Value;
                    if (placement.bin == null) continue;
                    var key = (island.mesh, island.channel);
                    if (!meshChannels.TryGetValue(key, out var list))
                    {
                        list = new List<ATOIsland>();
                        meshChannels[key] = list;
                    }
                    list.Add(island);
                }
            }
            if (meshChannels.Count == 0) return;

            // 网格克隆缓存 / mesh clone cache
            var cloneCache = new Dictionary<Mesh, Mesh>();
            var uvCache = new Dictionary<(Mesh, int), List<Vector2>>();

            foreach (var kv in meshChannels)
            {
                var mesh = kv.Key.Item1;
                var channel = kv.Key.Item2;
                var islands = kv.Value;
                var newMesh = CloneMesh(mesh, cloneCache, context);

                var uvs = GetUvs(mesh, channel, uvCache);
                var newUvs = new List<Vector2>(uvs);

                foreach (var island in islands)
                {
                    if (!TryGetPlacement(island, groups, out var placement)) continue;
                    RewriteIsland(mesh, channel, island, placement, newUvs);
                }
                newMesh.SetUVs(channel, newUvs);

                // AAO 兼容：原 UV 迁移到空闲通道 / AAO compat: evacuate original UVs into a free channel
                if (evacuationMap != null)
                {
                    foreach (var renderer in renderers)
                    {
                        if (renderer.mesh != mesh) continue;
                        if (evacuationMap.TryGetValue(renderer.renderer, out var evac) && evac.TryGetValue(channel, out var savedChannel))
                        {
                            newMesh.SetUVs(savedChannel, uvs); // 原 UV 保存 / save original UVs
                            if (renderer.renderer is SkinnedMeshRenderer smr2)
                                ATOAAOCompat.RegisterTexCoordEvacuation(smr2, channel, savedChannel);
                        }
                    }
                }

                // 网格替换 / mesh replacement
                foreach (var renderer in renderers)
                {
                    if (renderer.mesh != mesh) continue;
                    if (renderer.renderer is SkinnedMeshRenderer smr) smr.sharedMesh = newMesh;
                    else
                    {
                        var mf = renderer.renderer.GetComponent<MeshFilter>();
                        if (mf != null) mf.sharedMesh = newMesh;
                    }
                }
            }
        }

        private static bool TryGetPlacement(ATOIsland island, List<ATOTypeGroup> groups, out ATOPlacement placement)
        {
            foreach (var g in groups)
            {
                if (g.layout.TryGetValue(island, out placement) && placement.bin != null)
                    return true;
            }
            placement = null;
            return false;
        }

        /// <summary>重写一个岛的全部顶点 UV。/ Rewrite all vertex UVs of one island.</summary>
        private static void RewriteIsland(Mesh mesh, int channel, ATOIsland island, ATOPlacement placement, List<Vector2> uvs)
        {
            var bmin = island.uvMin + island.translation;
            var bmax = island.uvMax + island.translation;
            var span = bmax - bmin;
            var invSpan = new Vector2(span.x > 1e-6f ? 1f / span.x : 0f, span.y > 1e-6f ? 1f / span.y : 0f);

            var tris = mesh.triangles;
            var seen = new HashSet<int>();
            foreach (var t in island.triangles)
            {
                for (int e = 0; e < 3; e++)
                {
                    var vi = tris[t * 3 + e];
                    if (!seen.Add(vi)) continue;
                    var uv = uvs[vi];
                    var n = new Vector2((uv.x - bmin.x) * invSpan.x, (uv.y - bmin.y) * invSpan.y);
                    // 旋转 / rotation
                    Vector2 rn;
                    switch (placement.rotation & 3)
                    {
                        case 1: rn = new Vector2(1f - n.y, n.x); break;
                        case 2: rn = new Vector2(1f - n.x, 1f - n.y); break;
                        case 3: rn = new Vector2(n.y, 1f - n.x); break;
                        default: rn = n; break;
                    }
                    uvs[vi] = new Vector2(
                        placement.min.x + rn.x * placement.size.x,
                        placement.min.y + rn.y * placement.size.y);
                }
            }
        }

        private static Mesh CloneMesh(Mesh mesh, Dictionary<Mesh, Mesh> cache, BuildContext context)
        {
            if (cache.TryGetValue(mesh, out var clone)) return clone;
            clone = Object.Instantiate(mesh);
            clone.name = mesh.name;
            nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(mesh, clone);
            cache[mesh] = clone;
            return clone;
        }

        private static List<Vector2> GetUvs(Mesh mesh, int channel, Dictionary<(Mesh, int), List<Vector2>> cache)
        {
            var key = (mesh, channel);
            if (!cache.TryGetValue(key, out var uvs))
            {
                uvs = new List<Vector2>();
                mesh.GetUVs(channel, uvs);
                if (uvs.Count == 0)
                {
                    // 通道无数据时补齐（按顶点数）/ fill when the channel has no data
                    for (int i = 0; i < mesh.vertexCount; i++) uvs.Add(Vector2.zero);
                }
                cache[key] = uvs;
            }
            return uvs;
        }
    }
}
