// SPDX-License-Identifier: MIT
// EN: The pure texture-space to atlas-space mapping. Kept dependency free so it can be unit tested
//     outside of Unity, because getting the rotation convention wrong here silently corrupts every
//     rotated island.
// ZH: 纯粹的贴图空间到图集空间映射。刻意保持无依赖，以便在 Unity 之外做单元测试——
//     因为这里一旦把旋转约定弄错，所有被旋转的岛都会静默出错。

using Net.Fosa.AvatarTextureOptimizer.Editor.Model;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Apply
{
    /// <summary>
    /// EN: Maps UVs from a UV group's reference texture space into the atlas that its islands were
    ///     packed into.
    /// ZH: 将 UV 从某 UV 组的参考贴图空间映射到其岛被装入的图集中。
    /// </summary>
    public static class AtlasUvMapping
    {
        /// <summary>
        /// EN: Maps one UV. Honours the island's scale, its optional 90 degree rotation and its origin.
        ///
        ///     The rotation convention must match <c>Hidden/ATO/IslandBlit</c> exactly. That shader
        ///     samples <c>uv' = (v, 1 - u)</c>, and the quad it draws has the island's width and height
        ///     swapped. Working the inverse through: a point at island local (sx, sy) in a w by h island
        ///     is written at atlas offset (h - sy, sx) from the island origin.
        /// ZH: 映射一个 UV。正确处理岛的缩放、可选的 90 度旋转与原点。
        ///
        ///     旋转约定必须与 <c>Hidden/ATO/IslandBlit</c> 完全一致。该着色器采样
        ///     <c>uv' = (v, 1 - u)</c>，且它绘制的四边形交换了岛的宽与高。反推可得：
        ///     w×h 的岛中位于局部坐标 (sx, sy) 的点，会被写到相对岛原点 (h - sy, sx) 的图集位置上。
        /// </summary>
        /// <param name="uv">EN: UV in the group's reference space. ZH: 组参考空间中的 UV。</param>
        /// <param name="island">EN: The island the UV belongs to. ZH: 该 UV 所属的岛。</param>
        /// <param name="referenceSize">EN: Reference texture resolution. ZH: 参考贴图分辨率。</param>
        /// <param name="atlasSize">EN: Atlas resolution. ZH: 图集分辨率。</param>
        public static Vector2 MapToAtlas(Vector2 uv, UvIsland island, Vector2Int referenceSize, Vector2Int atlasSize)
        {
            float px = uv.x * referenceSize.x - island.Bounds.x;
            float py = uv.y * referenceSize.y - island.Bounds.y;

            float sx = px * (island.ScaledSize.x / (float)Mathf.Max(1, island.Bounds.width));
            float sy = py * (island.ScaledSize.y / (float)Mathf.Max(1, island.Bounds.height));

            float ax, ay;
            if (island.Rotated)
            {
                ax = island.AtlasOrigin.x + (island.ScaledSize.y - sy);
                ay = island.AtlasOrigin.y + sx;
            }
            else
            {
                ax = island.AtlasOrigin.x + sx;
                ay = island.AtlasOrigin.y + sy;
            }

            return new Vector2(ax / atlasSize.x, ay / atlasSize.y);
        }

        /// <summary>
        /// EN: The axis aligned rectangle, in atlas texels, that an island occupies after placement.
        ///     Rotation swaps the extents, which is what the packer's mask transpose also does.
        /// ZH: 岛放置后在图集中占据的轴对齐矩形（单位为图集像素）。
        ///     旋转会交换宽高，这与装箱器的掩码转置一致。
        /// </summary>
        public static RectInt PlacedRect(UvIsland island)
        {
            int w = island.Rotated ? island.ScaledSize.y : island.ScaledSize.x;
            int h = island.Rotated ? island.ScaledSize.x : island.ScaledSize.y;
            return new RectInt(island.AtlasOrigin.x, island.AtlasOrigin.y, w, h);
        }
    }
}
