using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: Rewrites mesh UVs so every island points at its new home inside the atlas.
    ///
    ///     The rewrite is purely a per-vertex affine map from the island's original UV bounding box onto
    ///     its packed rectangle, expressed in normalised atlas coordinates. A rotated island simply
    ///     swaps U and V; because we never touch positions, normals or tangents, tangent-space normal
    ///     maps stay valid without any recomputation.
    ///
    ///     Before rewriting, the original UVs of a channel Avatar Optimizer cares about are copied into
    ///     a free channel and registered through its evacuation API.
    ///
    /// ZH: 重写网格 UV，使每个岛指向它在图集中的新位置。
    ///
    ///     重写本质上是逐顶点的仿射映射：把岛原始 UV 包围盒映射到它的装箱矩形（以归一化图集坐标表示）。
    ///     旋转的岛只是交换 U 与 V；由于我们从不触碰位置、法线与切线，
    ///     切线空间法线贴图无需任何重算即保持有效。
    ///
    ///     重写之前，会把 Avatar Optimizer 关心的通道的原始 UV 复制到空闲通道，
    ///     并通过它的疏散 API 注册。
    /// </summary>
    public sealed class MeshRewriter
    {
        private readonly ATOLog _log;
        private readonly Dictionary<Mesh, Mesh> _clones = new Dictionary<Mesh, Mesh>();

        /// <summary>EN: Construct. ZH: 构造。</summary>
        public MeshRewriter(ATOLog log) { _log = log; }

        /// <summary>EN: Every mesh clone produced, so the caller can register them as build assets. ZH: 产生的所有网格克隆，供调用方登记为构建资产。</summary>
        public IEnumerable<Mesh> GeneratedMeshes => _clones.Values;

        /// <summary>
        /// EN: Apply the packed layout to every renderer touched by <paramref name="groups"/>.
        /// ZH: 把装箱布局应用到 <paramref name="groups"/> 涉及的所有渲染器。
        /// </summary>
        public void Apply(IEnumerable<UVGroup> groups, Dictionary<UVGroup, Vector2Int> atlasSizeOf)
        {
            // EN: Group by (mesh, uv channel), NOT by renderer. Two renderers can share one mesh asset;
            //     keying by renderer would make the second rewrite overwrite the first one's layout.
            // ZH: 按 (网格, UV 通道) 分组，而不是按渲染器。两个渲染器可能共享同一个网格资产；
            //     若按渲染器分组，第二次重写会覆盖掉第一次的布局。
            var byMesh = new Dictionary<(Mesh, int), List<(UVGroup g, MeshBinding b)>>();
            var renderersOfMesh = new Dictionary<(Mesh, int), List<Renderer>>();
            foreach (var g in groups)
            {
                if (g.SkipAtlas || g.FullyWhitelisted) continue;
                foreach (var b in g.Bindings)
                {
                    var m = GetMesh(b.Renderer);
                    if (m == null) continue;
                    var key = (m, b.UvChannel);
                    if (!byMesh.TryGetValue(key, out var list)) byMesh[key] = list = new List<(UVGroup, MeshBinding)>();
                    list.Add((g, b));
                    if (!renderersOfMesh.TryGetValue(key, out var rl)) renderersOfMesh[key] = rl = new List<Renderer>();
                    if (!rl.Contains(b.Renderer)) rl.Add(b.Renderer);
                }
            }

            foreach (var kv in byMesh)
            {
                var (mesh, channel) = kv.Key;
                var renderers = renderersOfMesh[kv.Key];
                foreach (var rdr in renderers) EvacuateForAAO(rdr, mesh, channel);

                var clone = GetClone(mesh);
                var uv = new List<Vector2>();
                clone.GetUVs(channel, uv);
                if (uv.Count == 0) continue;

                var uvArray = uv.ToArray();
                var written = new bool[uvArray.Length];

                foreach (var (group, binding) in kv.Value)
                {
                    if (!atlasSizeOf.TryGetValue(group, out var atlasSize)) continue;
                    var tris = clone.GetTriangles(binding.SubMesh);

                    foreach (var island in group.Islands)
                    {
                        if (island.AtlasIndex < 0) continue;
                        var span = island.UvMax - island.UvMin;
                        if (span.x <= 0f) span.x = 1e-6f;
                        if (span.y <= 0f) span.y = 1e-6f;

                        var rect = island.PackedRect;
                        float rx = rect.x / (float)atlasSize.x;
                        float ry = rect.y / (float)atlasSize.y;
                        float rw = rect.width / (float)atlasSize.x;
                        float rh = rect.height / (float)atlasSize.y;

                        foreach (var t in island.Triangles)
                        for (int k = 0; k < 3; k++)
                        {
                            int vi = tris[t + k];
                            if (written[vi]) continue;

                            // EN: Undo the wrap normalisation, then map into the packed rectangle.
                            // ZH: 先撤销 wrap 归一化，再映射进装箱矩形。
                            var p = uvArray[vi] - new Vector2(island.Wrap.x, island.Wrap.y);
                            float u = (p.x - island.UvMin.x) / span.x;
                            float v = (p.y - island.UvMin.y) / span.y;

                            if (island.PackedRotated) (u, v) = (v, u);

                            uvArray[vi] = new Vector2(rx + u * rw, ry + v * rh);
                            written[vi] = true;
                        }
                    }
                }

                clone.SetUVs(channel, uvArray);
                foreach (var rdr in renderers) ApplyMesh(rdr, clone);
                _log.Trace($"Rewrote UV{channel} of mesh '{mesh.name}' ({uvArray.Length} vertices, {renderers.Count} renderers)");
            }
        }

        private void EvacuateForAAO(Renderer renderer, Mesh mesh, int channel)
        {
            if (!(renderer is SkinnedMeshRenderer smr)) return;
            if (!AAOCompat.Available) return;
            if (!AAOCompat.IsTexCoordUsed(smr, channel)) return;

            // EN: Find a free channel AAO is not using either.
            // ZH: 找一个 AAO 也没在用的空闲通道。
            for (int c = 7; c >= 0; c--)
            {
                if (c == channel) continue;
                var probe = new List<Vector2>();
                mesh.GetUVs(c, probe);
                if (probe.Count != 0) continue;
                if (AAOCompat.IsTexCoordUsed(smr, c)) continue;

                var clone = GetClone(mesh);
                var src = new List<Vector2>();
                clone.GetUVs(channel, src);
                clone.SetUVs(c, src);
                if (AAOCompat.RegisterEvacuation(smr, channel, c, _log)) return;
            }
            _log.Warn($"Could not find a free UV channel to evacuate UV{channel} of '{renderer.name}' for Avatar Optimizer.");
        }

        private Mesh GetClone(Mesh original)
        {
            if (_clones.TryGetValue(original, out var c)) return c;
            c = UnityEngine.Object.Instantiate(original);
            c.name = original.name + " (ATO)";
            _clones[original] = c;
            return c;
        }

        private static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            return r.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null;
        }

        private static void ApplyMesh(Renderer r, Mesh m)
        {
            if (r is SkinnedMeshRenderer smr) smr.sharedMesh = m;
            else if (r.TryGetComponent<MeshFilter>(out var mf)) mf.sharedMesh = m;
        }
    }
}
