// ATO — Avatar Texture Optimizer
// Rewrites mesh UVs to point at the packed atlas positions, cloning meshes first so source
// assets are never mutated. When AAO is present, original UVs used by AAO are evacuated to
// a spare channel via UVUsageCompabilityAPI before rewriting.
// 重写网格 UV 使其指向装箱后的图集位置；先克隆网格以免改动源资产。存在 AAO 时，
// 在重写前通过 UVUsageCompabilityAPI 把 AAO 使用的原始 UV 疏散到备用通道。

using System.Collections.Generic;
using UnityEngine;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Mesh UV rewrite. 网格 UV 重写。
    /// </summary>
    public static class MeshRewriter
    {
        /// <summary>
        /// Rewrite UVs for all packed islands. 重写所有已装箱岛的 UV。
        /// </summary>
        public static void Rewrite(ATOBuildContext bc, ATOAnalysisResult result)
        {
            // island → placement (first occurrence across atlases). 岛 → 放置（跨图集取首个）。
            var placement = new Dictionary<ATOIsland, ATOPackedIsland>();
            foreach (var atlas in result.atlases)
                foreach (var p in atlas.packed)
                    if (!placement.ContainsKey(p.island)) placement[p.island] = p;

            // Group islands by renderer and channel. 按渲染器与通道分组岛。
            var byRenderer = new Dictionary<Renderer, Dictionary<int, List<ATOIsland>>>();
            foreach (var g in result.uvGroups)
            {
                if (g.whitelisted) continue;
                if (!byRenderer.TryGetValue(g.renderer, out var byChannel))
                {
                    byChannel = new Dictionary<int, List<ATOIsland>>();
                    byRenderer[g.renderer] = byChannel;
                }
                if (!byChannel.TryGetValue(g.uvChannel, out var list))
                {
                    list = new List<ATOIsland>();
                    byChannel[g.uvChannel] = list;
                }
                foreach (var island in g.islands)
                    if (placement.ContainsKey(island)) list.Add(island);
            }

            foreach (var kv in byRenderer)
            {
                var renderer = kv.Key;
                var mesh = GetSharedMesh(renderer);
                if (mesh == null) continue;

                Mesh clone = null; // clone lazily. 惰性克隆。

                foreach (var channelKv in kv.Value)
                {
                    int channel = channelKv.Key;
                    var islands = channelKv.Value;
                    if (islands.Count == 0) continue;

                    // AAO evacuation for this renderer+channel. 该渲染器+通道的 AAO 疏散。
                    if (renderer is SkinnedMeshRenderer smr && AAOIntegration.IsTexCoordUsed(smr, channel))
                    {
                        int spare = FindSpareChannel(result, renderer, channel);
                        if (spare >= 0)
                        {
                            EnsureClone(ref clone, mesh);
                            var originalUVs = new List<Vector2>();
                            clone.GetUVs(channel, originalUVs);
                            clone.SetUVs(spare, originalUVs);
                            AAOIntegration.RegisterTexCoordEvacuation(smr, channel, spare);
                            ATOLog.Verbose($"[AAO] evacuated UV{channel} → UV{spare} on '{renderer.name}'.");
                        }
                    }

                    EnsureClone(ref clone, mesh);
                    var uvs = new List<Vector2>();
                    clone.GetUVs(channel, uvs);

                    foreach (var island in islands)
                    {
                        if (island.scaledUV == null) continue;

                        bool atlased = placement.TryGetValue(island, out var p);
                        for (int i = 0; i < island.vertexIndices.Count; i++)
                        {
                            int vertex = island.vertexIndices[i];
                            if (vertex < 0 || vertex >= uvs.Count) continue;
                            // Atlased → atlas placement; otherwise scaled-in-place (original texture).
                            // 已装箱 → 图集位置；否则原地缩放（原贴图）。
                            uvs[vertex] = atlased
                                ? ComputeAtlasUV(island, island.scaledUV[i], p, p.island)
                                : island.scaledUV[i];
                        }
                    }
                    clone.SetUVs(channel, uvs);
                }

                if (clone != null)
                {
                    clone.name = mesh.name;
                    clone.RecalculateBounds();
                    // Recompute UV distribution metrics for mip-streaming (NDMF also does this for
                    // temporary assets, but our clones are runtime meshes — be explicit).
                    // 为 mip-streaming 重算 UV 分布度量（NDMF 对临时资产会做，但我们的克隆是运行时网格——显式处理）。
                    try { clone.RecalculateUVDistributionMetrics(); }
                    catch (System.Exception) { /* ignore 忽略 */ }
                    AssignMesh(renderer, clone);
                }
            }
        }

        private static Vector2 ComputeAtlasUV(ATOIsland island, Vector2 scaledUV, ATOPackedIsland p, ATOIsland islandRef)
        {
            float w = island.bounds.width * island.scaleX;
            float h = island.bounds.height * island.scaleY;
            float lx = w > 1e-9f ? (scaledUV.x - island.bounds.min.x) / w : 0f;
            float ly = h > 1e-9f ? (scaledUV.y - island.bounds.min.y) / h : 0f;

            int size = p.size.x; // square atlases. 方形图集。
            float px = lx * p.size.x;
            float py = ly * p.size.y;

            // Apply rotation (mirrors the bake). 应用旋转（与烘焙一致）。
            float rx = px, ry = py;
            switch (((p.rotationSteps % 4) + 4) % 4)
            {
                case 1: rx = p.size.y - py; ry = px; break;           // 90 CW
                case 2: rx = p.size.x - px; ry = p.size.y - py; break; // 180
                case 3: rx = py; ry = p.size.x - px; break;            // 270 CW
            }

            float atlasX = (p.offset.x + rx) / size;
            float atlasY = (p.offset.y + ry) / size;
            return new Vector2(Mathf.Clamp01(atlasX), Mathf.Clamp01(atlasY));
        }

        private static int FindSpareChannel(ATOAnalysisResult result, Renderer renderer, int exclude)
        {
            var used = new HashSet<int>();
            used.Add(exclude);
            foreach (var g in result.uvGroups)
            {
                if (g.renderer == renderer) used.Add(g.uvChannel);
            }
            if (renderer is SkinnedMeshRenderer smr)
            {
                for (int c = 0; c < 8; c++)
                    if (AAOIntegration.IsTexCoordUsed(smr, c)) used.Add(c);
            }
            for (int c = 0; c < 8; c++)
                if (!used.Contains(c)) return c;
            return -1;
        }

        private static void EnsureClone(ref Mesh clone, Mesh source)
        {
            if (clone == null) clone = Object.Instantiate(source);
        }

        private static Mesh GetSharedMesh(Renderer r)
        {
            switch (r)
            {
                case SkinnedMeshRenderer smr: return smr.sharedMesh;
                case MeshRenderer mr:
                    var mf = mr.GetComponent<MeshFilter>();
                    return mf != null ? mf.sharedMesh : null;
                default: return null;
            }
        }

        private static void AssignMesh(Renderer r, Mesh clone)
        {
            switch (r)
            {
                case SkinnedMeshRenderer smr: smr.sharedMesh = clone; break;
                case MeshRenderer mr:
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf != null) mf.sharedMesh = clone;
                    break;
            }
        }
    }
}
