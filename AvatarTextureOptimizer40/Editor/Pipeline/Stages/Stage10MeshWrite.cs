using System.Collections.Generic;
using UnityEngine;

namespace Fosa.Ato.Editor.Pipeline.Stages
{
    /// <summary>
    /// Stage 10: Rewrite mesh UVs so each island maps from its new atlas/standalone placement.
    /// If we modify a UV channel that AAO uses, evacuate the original UVs to a free channel via
    /// AAO's UVUsageCompabilityAPI (soft dependency via reflection; no-op if AAO isn't installed).
    /// We never touch non-UV data and never recompute normals/tangents.
    /// 阶段 10：重写网格 UV，使每个岛映射到其新图集/独立贴图位置。若修改了 AAO 使用的 UV 通道，
    /// 通过 AAO 的 UVUsageCompabilityAPI（反射软依赖，未安装则不处理）把原 UV 疏散到空闲通道。
    /// 不触碰非 UV 数据，绝不重算法线/切线。
    /// </summary>
    internal sealed class Stage10MeshWrite : IStage
    {
        public string Name => "ATO/10 Writing mesh UVs";
        public float Weight => 3f;

        public void Run(AtoPipeline p)
        {
            // Fast map: island -> its containing atlas/standalone and placement.
            // 快速映射：岛 -> 其所在图集/独立贴图与放置
            var islandToAtlas = new Dictionary<Island, AtlasResult>();
            var islandPlacement = new Dictionary<Island, PlacedIsland>();
            foreach (var atlas in p.Atlases)
                foreach (var pl in atlas.Placements)
                {
                    islandToAtlas[pl.Island] = atlas;
                    islandPlacement[pl.Island] = pl;
                }

            // (mesh, channel) -> placements on that mesh/channel / 按（网格，通道）分组
            var byMesh = new Dictionary<(Mesh, int), List<Island>>();
            foreach (var island in islandToAtlas.Keys)
            {
                var key = (island.Uv.Mesh, island.Uv.Channel);
                if (!byMesh.TryGetValue(key, out var list)) byMesh[key] = list = new List<Island>();
                list.Add(island);
            }

            // Only clone meshes for renderers that actually have a non-whitelisted eligible texture.
            // 仅克隆确实有合格贴图的渲染器的网格
            var affectedRenderers = new HashSet<Renderer>();
            foreach (var slot in p.SlotTextures)
            {
                if (slot.Key.Renderer == null) continue;
                foreach (var u in slot.Value)
                    if (u != null && !u.Whitelisted) { affectedRenderers.Add(slot.Key.Renderer); break; }
            }

            foreach (var r in affectedRenderers)
            {
                p.Progress.ThrowIfCancelled();
                Mesh src = r is SkinnedMeshRenderer smr
                    ? smr.sharedMesh
                    : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (src == null) continue;

                var dst = Object.Instantiate(src);
                dst.name = src.name + "_ATO";
                p.Ctx.AssetSaver.SaveAsset(dst);

                for (int ch = 0; ch < 8; ch++)
                {
                    if (!byMesh.TryGetValue((src, ch), out var list)) continue;
                    var uvs = GetUv(dst, ch);
                    if (uvs == null || uvs.Length == 0) continue;

                    foreach (var isl in list)
                    {
                        if (!islandPlacement.TryGetValue(isl, out var pl)) continue;
                        if (!islandToAtlas.TryGetValue(isl, out var atlas)) continue;
                        if (atlas.Width <= 0 || atlas.Height <= 0) continue;

                        float ax = (float)pl.PixelRect.x / atlas.Width;
                        float ay = (float)pl.PixelRect.y / atlas.Height;
                        float aw = (float)pl.PixelRect.width / atlas.Width;
                        float ah = (float)pl.PixelRect.height / atlas.Height;

                        foreach (var vi in isl.Triangles)
                        {
                            if ((uint)vi >= uvs.Length) continue;
                            var v = uvs[vi];
                            float nx = Mathf.InverseLerp(isl.UvBox.xMin, isl.UvBox.xMax, v.x);
                            float ny = Mathf.InverseLerp(isl.UvBox.yMin, isl.UvBox.yMax, v.y);
                            uvs[vi] = new Vector2(ax + nx * aw, ay + ny * ah);
                        }
                    }
                    SetUv(dst, ch, uvs);

                    if (r is SkinnedMeshRenderer smr2)
                        AaoBridge.EvacuateIfNeeded(smr2, ch);
                }

                if (r is SkinnedMeshRenderer s) s.sharedMesh = dst;
                else if (r.TryGetComponent<MeshFilter>(out var mf)) mf.sharedMesh = dst;
            }
        }

        private static Vector2[] GetUv(Mesh m, int ch) => ch switch
        {
            0 => m.uv, 1 => m.uv2, 2 => m.uv3, 3 => m.uv4,
            4 => m.uv5, 5 => m.uv6, 6 => m.uv7, 7 => m.uv8,
            _ => null,
        };
        private static void SetUv(Mesh m, int ch, Vector2[] uvs)
        {
            switch (ch)
            {
                case 0: m.uv = uvs; break; case 1: m.uv2 = uvs; break;
                case 2: m.uv3 = uvs; break; case 3: m.uv4 = uvs; break;
                case 4: m.uv5 = uvs; break; case 5: m.uv6 = uvs; break;
                case 6: m.uv7 = uvs; break; case 7: m.uv8 = uvs; break;
            }
        }
    }
}
