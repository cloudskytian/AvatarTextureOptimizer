// AvatarTextureOptimizer - MeshRemapBuilder
// EN: After packing, assigns each island its atlas UV rect (anchor = albedo texture of the UV group; the uniform
// per-type scaling guarantees the same uv rect in every atlas of the group within 4px rounding).
// CN: 装箱后为每个岛分配图集 UV 矩形（锚 = UV 组的主色贴图；均匀类型缩放保证组内各图集 uv 矩形一致，4px 舍入内）。
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    public static class MeshRemapBuilder
    {
        /// <summary>EN: Fills island.remapRect from the packing result. Anchor = the albedo instance of the UV group
        /// (uniform per-type scaling keeps the uv rect identical in every atlas of the group within 4px rounding).
        /// CN: 从装箱结果填充 island.remapRect。锚 = UV 组的主色实例（均匀类型缩放保证组内各图集 uv 矩形一致，4px 舍入内）。</summary>
        public static void AssignRemaps(AtoBuildState state, PackingResult packing)
        {
            // EN: Albedo atlases first so the anchor prefers albedo.
            // CN: 主色图集优先，使锚优先为主色。
            var sorted = new System.Collections.Generic.List<PackedAtlas>(packing.atlases);
            sorted.Sort((a, b) => ((int)a.usage).CompareTo((int)b.usage));

            foreach (var atlas in sorted)
            {
                Vector2 inv = new Vector2(1f / atlas.width, 1f / atlas.height);
                foreach (var pi in atlas.islands)
                {
                    var island = pi.island;
                    if (island.hasRemap) continue; // 保留首个（主色优先）
                    if (pi.tex.whitelisted || pi.tex.specialUv) continue;
                    var rect = pi.rect;
                    island.remapRect = new Rect(rect.x * inv.x, rect.y * inv.y,
                        rect.width * inv.x, rect.height * inv.y);
                    island.hasRemap = true;
                }
            }
        }
    }
}
