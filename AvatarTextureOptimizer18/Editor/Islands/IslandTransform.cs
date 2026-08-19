using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Islands
{
    // 岛旋转与 UV 重映射约定（装箱、图集写入、网格 UV 重写三处共用，保证一致）。
    // Island rotation & UV remapping convention (shared by packing, atlas writing and mesh UV rewriting).
    //
    // rotation ∈ {0,1,2,3} = 内容逆时针旋转 r*90°（视觉 CCW；位掩码 Rotate90 同约定）。
    // rotation ∈ {0,1,2,3} = content rotated r*90° CCW (visually; matches BitMask.Rotate90).
    // 局部像素 (lx,ly) ∈ [0,pw)×[0,ph) → 旋转后内容坐标。Local pixel → rotated content coordinate.
    public static class IslandTransform
    {
        public static Vector2 LocalToContent(Vector2 local, Vector2Int localSize, int rotation)
        {
            float lx = local.x, ly = local.y;
            int pw = localSize.x, ph = localSize.y;
            switch (rotation & 3)
            {
                case 1: return new Vector2(ly, ph - 1 - lx);
                case 2: return new Vector2(pw - 1 - lx, ph - 1 - ly);
                case 3: return new Vector2(ly, pw - 1 - lx);
                default: return new Vector2(lx, ly);
            }
        }

        public static Vector2 ContentToLocal(Vector2 content, Vector2Int localSize, int rotation)
        {
            float cx = content.x, cy = content.y;
            int pw = localSize.x, ph = localSize.y;
            switch (rotation & 3)
            {
                case 1: return new Vector2(ph - 1 - cy, cx);
                case 2: return new Vector2(pw - 1 - cx, ph - 1 - cy);
                case 3: return new Vector2(pw - 1 - cy, cx);
                default: return new Vector2(cx, cy);
            }
        }

        // 旋转后的内容尺寸。Content size after rotation.
        public static Vector2Int RotatedSize(Vector2Int localSize, int rotation)
        {
            if ((rotation & 1) == 1) return new Vector2Int(localSize.y, localSize.x);
            return localSize;
        }

        // 网格 UV 重写：把（归一化后的）岛局部 UV 映射到图集 UV。Mesh UV rewrite: island-local (normalized) UV → atlas UV.
        // localUV = uv + translation - uvMin（归一化后岛内坐标）；span = 岛 UV 跨度；scale = 质量缩放；texSize = 锚定贴图分辨率。
        // 与 AtlasBuilder.WriteIsland 的像素尺寸公式一致（pw = ceil(span*scale*texSize)），避免 ±1px 舍入偏差。
        // localUV = uv + translation - uvMin; span = island UV span; scale = quality scale; texSize = anchor texture resolution.
        // Pixel sizes match AtlasBuilder.WriteIsland (pw = ceil(span*scale*texSize)); avoids ±1px rounding drift.
        public static Vector2 MapToAtlasUv(Vector2 localUV, Vector2 span, Vector2 scale, Vector2Int texSize,
            Vector2Int rectPosPx, Vector2Int atlasSizePx, int paddingPx, int rotation)
        {
            var localPx = new Vector2(localUV.x * span.x * scale.x * texSize.x, localUV.y * span.y * scale.y * texSize.y);
            var scaledSize = new Vector2Int(
                Mathf.Max(1, Mathf.CeilToInt(span.x * scale.x * texSize.x)),
                Mathf.Max(1, Mathf.CeilToInt(span.y * scale.y * texSize.y)));
            var content = LocalToContent(localPx, scaledSize, rotation);
            var origin = new Vector2(rectPosPx.x + paddingPx, rectPosPx.y + paddingPx);
            return new Vector2((origin.x + content.x) / atlasSizePx.x, (origin.y + content.y) / atlasSizePx.y);
        }
    }
}
