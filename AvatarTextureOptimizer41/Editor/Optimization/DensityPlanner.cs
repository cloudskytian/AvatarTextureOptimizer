using UnityEngine;

// Pixel-density planning: maps island world size (meters) to texel density (px/m) bounds,
// so islands are neither wasted (above max px/m) nor blurry (below min px/m).
// 像素密度规划：将岛的世界尺寸（米）映射到纹素密度（px/m）区间，防止浪费（超过最大 px/m）或发糊（低于最小 px/m）。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class DensityPlanner
    {
        /// <summary>
        /// Returns the allowed scale range [minScale, maxScale] for an island so the resulting texel
        /// density stays within [minPxPerMeter, maxPxPerMeter], and never upscales (<= 1).
        /// 返回岛的允许缩放区间 [minScale, maxScale]，使纹素密度保持在 [最小px/m, 最大px/m]，且永不上采样（<=1）。
        /// </summary>
        public static (float minScale, float maxScale) ScaleBounds(Vector2 worldSizeMeters, Vector2Int origPx, ATOSettingsData data)
        {
            float worldMeters = Mathf.Max(worldSizeMeters.x, worldSizeMeters.y);
            float minPPM = data.densityMinPxPerMeter, maxPPM = data.densityMaxPxPerMeter;
            if (worldMeters <= 1e-5f) return (0.1f, 1f);
            float largestOrigPx = Mathf.Max(1, Mathf.Max(origPx.x, origPx.y));
            float minPx = worldMeters * minPPM;
            float maxPx = worldMeters * maxPPM;
            float minScale = Mathf.Clamp01(minPx / largestOrigPx);
            float maxScale = Mathf.Clamp01(maxPx / largestOrigPx);
            if (minScale > maxScale) minScale = maxScale;
            return (minScale, maxScale);
        }

        /// <summary>Worst-case texel density in px/m for reporting. 报告用最差纹素密度（px/m）。</summary>
        public static float CurrentDensity(Vector2 worldSizeMeters, Vector2Int texPx)
        {
            float worldMeters = Mathf.Max(worldSizeMeters.x, worldSizeMeters.y);
            if (worldMeters <= 1e-5f) return float.MaxValue;
            return Mathf.Max(texPx.x, texPx.y) / worldMeters;
        }
    }
}
