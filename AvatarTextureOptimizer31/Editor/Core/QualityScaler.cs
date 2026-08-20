// QualityScaler.cs
// Phase 5: Scales UV islands to the maximum size that still meets the target quality.
// Uses binary search on the scale factor. Handles anisotropic refinement.
// Pure-color islands short-circuit to minimum size.
// Near-lossless preset skips scaling entirely.
// 阶段5：将 UV 岛缩放到仍满足目标质量的最大尺寸。使用二分搜索。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using UnityEngine;
using Fosa.AvatarTextureOptimizer.Quality;

namespace Fosa.AvatarTextureOptimizer.Core
{
    /// <summary>
    /// Performs quality-based UV island scaling using binary search.
    /// 对 UV 岛执行基于质量的二分搜索缩放。
    /// </summary>
    internal sealed class QualityScaler
    {
        private readonly List<UVGroup> _uvGroups;
        private readonly List<TextureTypeGroup> _typeGroups;
        private readonly AdvancedSettings _settings;
        private readonly ATOComponent _component;
        private readonly ATOLogger _log;
        private readonly QualityEvaluator _evaluator;

        internal QualityScaler(List<UVGroup> uvGroups, List<TextureTypeGroup> typeGroups,
            AdvancedSettings settings, ATOComponent component, ATOLogger log)
        {
            _uvGroups = uvGroups;
            _typeGroups = typeGroups;
            _settings = settings;
            _component = component;
            _log = log;
            _evaluator = new QualityEvaluator(settings);
        }

        internal int Execute()
        {
            // Near-lossless: skip all scaling (copy as-is)
            if (_component._qualityPreset == QualityPreset.NearLossless)
            {
                _log.Info("Near-lossless preset: skipping UV scaling entirely. / 近无损挡位：完全跳过 UV 缩放。");
                // Keep all islands at original size
                foreach (var ug in _uvGroups)
                    foreach (var island in ug.Islands)
                        island.ScaledPixelBounds = island.PixelBounds;
                return 0;
            }

            int scaledCount = 0;

            foreach (var ug in _uvGroups)
            {
                if (ug.IsWhitelisted) continue;

                // Compute target dimension for the UV group (wood-barrel effect)
                int groupTarget = 0;

                foreach (var island in ug.Islands)
                {
                    int origShortEdge = Mathf.RoundToInt(Mathf.Min(island.PixelBounds.width, island.PixelBounds.height));
                    if (origShortEdge < _settings.ignoreIslandThreshold)
                    {
                        island.ScaledPixelBounds = island.PixelBounds;
                        continue;
                    }

                    // Check for pure-color island short-circuit
                    if (IsPureColor(island.SourceTexture, island.PixelBounds))
                    {
                        int minSize = Mathf.Min(4, origShortEdge);
                        float scale = (float)minSize / origShortEdge;
                        island.ScaledPixelBounds = ScaleRect(island.PixelBounds, scale);
                        island.AnisotropicScale = new Vector2(scale, scale);
                        _log.Verbose($"Pure-color island {island} short-circuited to {minSize}px.");
                        scaledCount++;
                        continue;
                    }

                    // Binary search: uniform scale first
                    float uniformScale = BinarySearchScale(island);

                    // Clamp by pixel density limits
                    uniformScale = ClampByPixelDensity(island, uniformScale);

                    // Anisotropic refinement: independent U and V search
                    var anisoScale = AnisotropicRefine(island, uniformScale);

                    island.AnisotropicScale = anisoScale;
                    island.ScaledPixelBounds = new Rect(
                        island.PixelBounds.x,
                        island.PixelBounds.y,
                        Mathf.Max(2, island.PixelBounds.width * anisoScale.x),
                        Mathf.Max(2, island.PixelBounds.height * anisoScale.y)
                    );

                    int islandTarget = Mathf.RoundToInt(Mathf.Max(
                        island.ScaledPixelBounds.width, island.ScaledPixelBounds.height));
                    if (islandTarget > groupTarget) groupTarget = islandTarget;

                    scaledCount++;
                    _log.Verbose($"Island {island}: scale={anisoScale} → {island.ScaledPixelBounds.width:F0}×{island.ScaledPixelBounds.height:F0}");
                }

                // Wood-barrel: group target = max across all islands/textures
                ug.TargetDimension = Mathf.Min(groupTarget, ug.MaxOriginalDimension);
            }

            return scaledCount;
        }

        /// <summary>
        /// Binary search for the maximum scale factor (0-1) that meets quality thresholds.
        /// 二分搜索满足质量阈值的最大缩放因子。
        /// </summary>
        private float BinarySearchScale(UVIsland island)
        {
            const int maxIter = 12;
            float lo = 0.05f;  // minimum 5%
            float hi = 1.0f;   // original size
            float best = 1.0f;

            for (int iter = 0; iter < maxIter; iter++)
            {
                float mid = (lo + hi) / 2f;
                if (QualityPassesAtScale(island, mid, mid))
                {
                    best = mid;
                    lo = mid; // try larger
                }
                else
                {
                    hi = mid; // try smaller
                }
            }

            return best;
        }

        /// <summary>
        /// Anisotropic refinement: after uniform scale passes, independently
        /// reduce U and V to find the tightest fit.
        /// 各向异性细化：先均匀缩放至达标，再独立细化双轴。
        /// </summary>
        private Vector2 AnisotropicRefine(UVIsland island, float uniformScale)
        {
            float u = uniformScale;
            float v = uniformScale;

            // Refine U axis
            const int maxIter = 8;
            float lo = uniformScale, hi = 1.0f;
            for (int i = 0; i < maxIter; i++)
            {
                float mid = (lo + hi) / 2f;
                if (QualityPassesAtScale(island, mid, v))
                {
                    u = mid;
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            // Refine V axis
            lo = uniformScale; hi = 1.0f;
            for (int i = 0; i < maxIter; i++)
            {
                float mid = (lo + hi) / 2f;
                if (QualityPassesAtScale(island, u, mid))
                {
                    v = mid;
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            return new Vector2(u, v);
        }

        /// <summary>
        /// Tests whether the quality thresholds pass at a given scale factor.
        /// Tests the scaled-then-bilinearly-upscaled island against the original.
        /// 测试在给定缩放因子下质量阈值是否达标。
        /// </summary>
        private bool QualityPassesAtScale(UVIsland island, float scaleX, float scaleY)
        {
            var tex = island.SourceTexture;
            if (tex == null) return true;

            int origW = Mathf.RoundToInt(island.PixelBounds.width);
            int origH = Mathf.RoundToInt(island.PixelBounds.height);

            int scaledW = Mathf.Max(1, Mathf.RoundToInt(origW * scaleX));
            int scaledH = Mathf.Max(1, Mathf.RoundToInt(origH * scaleY));

            if (scaledW >= origW && scaledH >= origH) return true; // no scaling needed

            // Sample original and scaled pixels
            var origPixels = SampleTextureRegion(tex, island.PixelBounds, origW, origH);
            if (origPixels.Length == 0) return true;

            // Downsample (bilinear) then upsample (bilinear) back to original size
            var scaledPixels = DownsampleRegion(origPixels, origW, origH, scaledW, scaledH);
            var upsampled = UpsampleRegion(scaledPixels, scaledW, scaledH, origW, origH);

            // Evaluate quality
            var scanRef = island.SourceTexture;
            bool hasAlpha = GraphicsFormatUtility.HasAlphaChannel(tex.graphicsFormat);

            AlphaMode alphaMode = AlphaMode.Opaque;
            float cutoff = 0.5f;

            QualityResult result;
            try
            {
                result = _evaluator.EvaluateColor(origPixels, upsampled, origW, origH,
                    scaledW, scaledH, alphaMode, cutoff, hasAlpha);
            }
            finally
            {
                // Dispose all NativeArrays to prevent memory leaks
                if (origPixels.IsCreated) origPixels.Dispose();
                if (scaledPixels.IsCreated) scaledPixels.Dispose();
                if (upsampled.IsCreated) upsampled.Dispose();
            }

            return result.Passes;
        }

        private float ClampByPixelDensity(UVIsland island, float scale)
        {
            // Ensure pixel density stays within [min, max] range
            float currentDensity = island.PixelDensity * scale;
            float maxDensity = _component._maxPixelDensity;
            float minDensity = _component._minPixelDensity;

            if (currentDensity > maxDensity)
            {
                scale = maxDensity / island.PixelDensity;
            }
            if (currentDensity < minDensity * scale)
            {
                // Don't upscale beyond original
                scale = Mathf.Max(scale, minDensity / island.PixelDensity);
            }

            return Mathf.Clamp01(scale);
        }

        private bool IsPureColor(Texture2D tex, Rect region)
        {
            if (tex == null || !tex.isReadable) return false;
            try
            {
                int x = Mathf.RoundToInt(region.x);
                int y = Mathf.RoundToInt(region.y);
                int w = Mathf.Min(Mathf.RoundToInt(region.width), tex.width - x);
                int h = Mathf.Min(Mathf.RoundToInt(region.height), tex.height - y);
                if (w <= 0 || h <= 0) return false;

                var pixels = tex.GetPixels(x, y, w, h, 0);
                if (pixels.Length <= 1) return true;

                var first = pixels[0];
                const float tol = 0.004f; // ~1/255
                for (int i = 1; i < pixels.Length; i++)
                {
                    if (Mathf.Abs(pixels[i].r - first.r) > tol ||
                        Mathf.Abs(pixels[i].g - first.g) > tol ||
                        Mathf.Abs(pixels[i].b - first.b) > tol ||
                        Mathf.Abs(pixels[i].a - first.a) > tol)
                        return false;
                }
                return true;
            }
            catch { return false; }
        }

        private NativeArray<Color32> SampleTextureRegion(Texture2D tex, Rect region, int targetW, int targetH)
        {
            var result = new NativeArray<Color32>(targetW * targetH, Allocator.Persistent);
            try
            {
                if (tex.isReadable)
                {
                    int x = Mathf.Clamp(Mathf.RoundToInt(region.x), 0, tex.width - 1);
                    int y = Mathf.Clamp(Mathf.RoundToInt(region.y), 0, tex.height - 1);
                    int w = Mathf.Clamp(Mathf.RoundToInt(region.width), 1, tex.width - x);
                    int h = Mathf.Clamp(Mathf.RoundToInt(region.height), 1, tex.height - y);

                    var pixels = tex.GetPixels(x, y, w, h);
                    // Resample to targetW x targetH
                    for (int ty = 0; ty < targetH; ty++)
                    {
                        for (int tx = 0; tx < targetW; tx++)
                        {
                            float u = (float)tx / Mathf.Max(1, targetW - 1) * (w - 1);
                            float v = (float)ty / Mathf.Max(1, targetH - 1) * (h - 1);
                            int sx = Mathf.Clamp(Mathf.RoundToInt(u), 0, w - 1);
                            int sy = Mathf.Clamp(Mathf.RoundToInt(v), 0, h - 1);
                            var c = pixels[sy * w + sx];
                            result[ty * targetW + tx] = new Color32(
                                (byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255), (byte)(c.a * 255));
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        private NativeArray<Color32> DownsampleRegion(NativeArray<Color32> src, int srcW, int srcH, int dstW, int dstH)
        {
            var dst = new NativeArray<Color32>(dstW * dstH, Allocator.Persistent);
            for (int y = 0; y < dstH; y++)
            {
                for (int x = 0; x < dstW; x++)
                {
                    float u = (float)x / Mathf.Max(1, dstW - 1) * (srcW - 1);
                    float v = (float)y / Mathf.Max(1, dstH - 1) * (srcH - 1);
                    int x0 = Mathf.FloorToInt(u), y0 = Mathf.FloorToInt(v);
                    int x1 = Mathf.Min(x0 + 1, srcW - 1), y1 = Mathf.Min(y0 + 1, srcH - 1);
                    float fx = u - x0, fy = v - y0;

                    var c00 = src[y0 * srcW + x0];
                    var c01 = src[y0 * srcW + x1];
                    var c10 = src[y1 * srcW + x0];
                    var c11 = src[y1 * srcW + x1];

                    dst[y * dstW + x] = Blerp(c00, c01, c10, c11, fx, fy);
                }
            }
            return dst;
        }

        private NativeArray<Color32> UpsampleRegion(NativeArray<Color32> src, int srcW, int srcH, int dstW, int dstH)
        {
            return DownsampleRegion(src, srcW, srcH, dstW, dstH); // bilinear either way
        }

        private static Color32 Blerp(Color32 c00, Color32 c01, Color32 c10, Color32 c11, float fx, float fy)
        {
            float r = (c00.r * (1 - fx) + c01.r * fx) * (1 - fy) + (c10.r * (1 - fx) + c11.r * fx) * fy;
            float g = (c00.g * (1 - fx) + c01.g * fx) * (1 - fy) + (c10.g * (1 - fx) + c11.g * fx) * fy;
            float b = (c00.b * (1 - fx) + c01.b * fx) * (1 - fy) + (c10.b * (1 - fx) + c11.b * fx) * fy;
            float a = (c00.a * (1 - fx) + c01.a * fx) * (1 - fy) + (c10.a * (1 - fx) + c11.a * fx) * fy;
            return new Color32((byte)r, (byte)g, (byte)b, (byte)a);
        }

        private static Rect ScaleRect(Rect r, float s)
        {
            return new Rect(r.x, r.y, r.width * s, r.height * s);
        }
    }
}
