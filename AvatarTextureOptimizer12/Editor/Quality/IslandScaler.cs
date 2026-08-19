// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Island extraction, normal codec and the quality-driven scale search.
// AvatarTextureOptimizer (ATO) - 岛提取、法线编解码，以及由质量驱动的缩放搜索。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.MeshOps;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// EN: Tangent-space normal encoding/decoding. Unity stores desktop normal maps as DXT5nm (x in A,
    ///     y in G) and mobile normal maps as plain RGB. We detect which one we are looking at from the
    ///     channel statistics rather than guessing from the platform, then always work with unit vectors.
    /// ZH: 切线空间法线的编解码。Unity 在桌面端以 DXT5nm 存储法线（x 在 A，y 在 G），移动端则为普通 RGB。
    ///     我们从通道统计量判断属于哪一种，而不是靠平台猜测，之后一律使用单位向量参与计算。
    /// </summary>
    public static class NormalCodec
    {
        /// <summary>EN: True when the texture uses the DXT5nm layout. ZH: 贴图使用 DXT5nm 布局时为 true。</summary>
        public static bool IsDxt5nm(TextureContentInfo info)
        {
            // EN: In DXT5nm the R and B channels carry no information and alpha varies.
            // ZH: DXT5nm 中 R 与 B 通道不携带信息，而 alpha 有变化。
            bool rVaries = (info.VaryingChannels & 1) != 0;
            bool bVaries = (info.VaryingChannels & 4) != 0;
            bool aVaries = (info.VaryingChannels & 8) != 0;
            return aVaries && !rVaries && !bVaries;
        }

        public static float3 Decode(Color32 c, bool dxt5nm)
        {
            float x, y;
            if (dxt5nm)
            {
                x = c.a / 255f * 2f - 1f;
                y = c.g / 255f * 2f - 1f;
            }
            else
            {
                x = c.r / 255f * 2f - 1f;
                y = c.g / 255f * 2f - 1f;
            }
            float z = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y));
            var v = new float3(x, y, z);
            return math.normalizesafe(v, new float3(0, 0, 1));
        }

        public static Color32 Encode(float3 n, bool dxt5nm)
        {
            n = math.normalizesafe(n, new float3(0, 0, 1));
            byte bx = (byte)Mathf.Clamp(Mathf.RoundToInt((n.x * 0.5f + 0.5f) * 255f), 0, 255);
            byte by = (byte)Mathf.Clamp(Mathf.RoundToInt((n.y * 0.5f + 0.5f) * 255f), 0, 255);
            byte bz = (byte)Mathf.Clamp(Mathf.RoundToInt((n.z * 0.5f + 0.5f) * 255f), 0, 255);
            return dxt5nm ? new Color32(255, by, 255, bx) : new Color32(bx, by, bz, 255);
        }
    }

    /// <summary>
    /// EN: Per-island scaling decision, plus everything needed to reproduce it during atlas baking.
    /// ZH: 每个岛的缩放决策，以及在图集烘焙时复现它所需的一切信息。
    /// </summary>
    public sealed class IslandPlan
    {
        public TextureUsage Texture;
        public UVIslandSet Set;
        public UVIsland Island;

        /// <summary>EN: Source-texture pixel rect of the island bounding box. ZH: 岛包围盒在源贴图上的像素矩形。</summary>
        public RectInt SourceRect;

        public QualityResult LastResult;

        /// <summary>
        /// EN: Footprint this *particular texture* would like, before the UV group's bucket effect is
        ///     applied. The group then takes the maximum and writes it onto the shared island.
        /// ZH: 这张**具体贴图**希望得到的占位，尚未应用 UV 组的木桶效应。
        ///     之后由组取最大值并写回到共享的岛上。
        /// </summary>
        public int DesiredWidth, DesiredHeight;

        /// <summary>EN: Index of the atlas this texture's islands landed in, or -1. ZH: 该贴图的岛所在图集索引，未装箱为 -1。</summary>
        public int AtlasIndex = -1;

        /// <summary>EN: True when the island was short-circuited as a solid colour. ZH: 岛被判定为纯色并短路处理时为 true。</summary>
        public bool SolidShortCircuit;
    }

    public static class IslandScaler
    {
        /// <summary>
        /// EN: Extract an island's bounding-box region from the source texture into a linear image.
        ///     Colour textures are premultiplied when alpha matters; normal maps are decoded to unit vectors;
        ///     grayscale/data textures keep raw linear channel values.
        /// ZH: 从源贴图中提取岛包围盒区域并转为线性图像。
        ///     当 alpha 有意义时颜色做预乘；法线贴图解码为单位向量；灰度/数据贴图保留原始线性通道值。
        /// </summary>
        public static LinearImage ExtractIsland(TextureUsage usage, RectInt rect, NativeArray<Color32> pixels,
            int texWidth, int texHeight)
        {
            bool premultiply = usage.AlphaMode != ATOAlphaMode.Opaque && usage.Content.HasAlpha
                               && !usage.IsNormalMap;
            var img = new LinearImage(rect.width, rect.height, premultiply);

            bool dxt5nm = usage.IsNormalMap && NormalCodec.IsDxt5nm(usage.Content);
            var lut = TextureIntrospection.SrgbLut;
            bool srgb = usage.SRGB && !usage.IsNormalMap;

            for (int y = 0; y < rect.height; y++)
            {
                int sy = Mathf.Clamp(rect.y + y, 0, texHeight - 1);
                for (int x = 0; x < rect.width; x++)
                {
                    int sx = Mathf.Clamp(rect.x + x, 0, texWidth - 1);
                    var c = pixels[sy * texWidth + sx];

                    float4 v;
                    if (usage.IsNormalMap)
                    {
                        var n = NormalCodec.Decode(c, dxt5nm);
                        v = new float4(n.x, n.y, n.z, 1f);
                    }
                    else if (srgb)
                    {
                        v = new float4(lut[c.r], lut[c.g], lut[c.b], c.a / 255f);
                    }
                    else
                    {
                        v = new float4(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
                    }

                    if (premultiply) v = new float4(v.xyz * v.w, v.w);
                    img[x, y] = v;
                }
            }
            return img;
        }

        /// <summary>
        /// EN: The scale search. Uniform binary search first (cheap and robust), then an independent binary
        ///     refinement per axis so that anisotropic islands do not waste pixels on their long axis.
        /// ZH: 缩放搜索。先做均匀二分（便宜且稳健），再对两个轴分别二分细化，
        ///     使各向异性的岛不会在长轴上浪费像素。
        /// </summary>
        public static void SolveScale(IslandPlan plan, ATOQualityParams p, LinearImage original,
            float worldArea, int texWidth, int texHeight)
        {
            var island = plan.Island;
            var usage = plan.Texture;

            // ---- Lossless tier: never rescale, never resample / 近无损挡位：不缩放也不重采样 ----
            if (p.lossless)
            {
                plan.DesiredWidth = island.PixelWidth;
                plan.DesiredHeight = island.PixelHeight;
                ATOLog.Trace($"[{usage.Texture.name}] island lossless, footprint kept at " +
                             $"{plan.DesiredWidth}x{plan.DesiredHeight}");
                return;
            }

            // ---- Solid-colour short circuit / 纯色短路 ----
            if (IsSolid(original))
            {
                int shortSide = Mathf.Min(island.PixelWidth, island.PixelHeight);
                int target = Mathf.Min(4, shortSide);
                plan.DesiredWidth = Mathf.Min(island.PixelWidth, target);
                plan.DesiredHeight = Mathf.Min(island.PixelHeight, target);
                plan.SolidShortCircuit = true;
                ATOLog.Trace($"[{usage.Texture.name}] solid island short-circuited to {target}px");
                return;
            }

            // ---- Density clamps / 像素密度钳制 ----
            float uvPixels = Mathf.Max(1f, island.UvArea * texWidth * texHeight);
            float minDensity = (float)p.minPixelDensity;
            float maxDensity = (float)p.maxPixelDensity;

            float sMin = 0.02f, sMax = 1f;
            if (worldArea > 1e-9f)
            {
                sMin = Mathf.Clamp(Mathf.Sqrt(worldArea * minDensity * minDensity / uvPixels), 0.01f, 1f);
                sMax = Mathf.Clamp(Mathf.Sqrt(worldArea * maxDensity * maxDensity / uvPixels), sMin, 1f);
            }

            // EN: We may never exceed the real, imported texture size - that would be pure waste.
            // ZH: 绝不能超过导入后的真实贴图尺寸——那是纯粹的浪费。
            sMax = Mathf.Min(sMax, 1f);

            int shortSidePx = Mathf.Min(island.PixelWidth, island.PixelHeight);
            var cutoffs = usage.Cutoffs.Count > 0 ? new List<float>(usage.Cutoffs).ToArray() : new[] { 0.5f };
            int grayMask = usage.Content.VaryingChannels != 0 ? usage.Content.VaryingChannels : 0xF;
            bool isGray = usage.Class == ATOTextureClass.Grayscale;

            bool Test(float sx, float sy, out QualityResult result)
            {
                int cw = Mathf.Max(1, Mathf.RoundToInt(island.PixelWidth * sx));
                int ch = Mathf.Max(1, Mathf.RoundToInt(island.PixelHeight * sy));
                var down = original.Downsample(cw, ch);
                var recon = down.UpsampleTo(original.Width, original.Height);
                result = QualityMetrics.Evaluate(original, recon, usage.IsNormalMap, isGray, grayMask,
                    usage.AlphaMode, cutoffs);
                return QualityMetrics.Passes(result, p, usage.IsNormalMap, isGray, usage.AlphaMode, shortSidePx);
            }

            // ---- Phase 1: uniform binary search / 阶段一：均匀二分搜索 ----
            float lo = sMin, hi = sMax;
            float best = sMax;
            QualityResult bestResult = default;

            if (!Test(sMax, sMax, out bestResult))
            {
                // EN: Even the maximum allowed scale fails; keep full resolution.
                // ZH: 连允许的最大缩放都不达标，保持原分辨率。
                plan.DesiredWidth = island.PixelWidth;
                plan.DesiredHeight = island.PixelHeight;
                plan.LastResult = bestResult;
                ATOLog.Trace($"[{usage.Texture.name}] island kept at 1.0 ({bestResult})");
                return;
            }

            for (int iter = 0; iter < 10 && hi - lo > 0.01f; iter++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Test(mid, mid, out var r))
                {
                    hi = mid;
                    best = mid;
                    bestResult = r;
                }
                else
                {
                    lo = mid;
                }
            }

            // ---- Phase 2: per-axis refinement / 阶段二：双轴独立细化 ----
            float bx = best, by = best;

            float loX = best * 0.4f, hiX = best;
            for (int iter = 0; iter < 6 && hiX - loX > 0.01f; iter++)
            {
                float mid = (loX + hiX) * 0.5f;
                if (Test(mid, by, out var r)) { hiX = mid; bx = mid; bestResult = r; }
                else loX = mid;
            }

            float loY = best * 0.4f, hiY = best;
            for (int iter = 0; iter < 6 && hiY - loY > 0.01f; iter++)
            {
                float mid = (loY + hiY) * 0.5f;
                if (Test(bx, mid, out var r)) { hiY = mid; by = mid; bestResult = r; }
                else loY = mid;
            }

            // EN: Re-clamp to the density floor after the anisotropic pass.
            // ZH: 各向异性细化之后重新按密度下限钳制。
            bx = Mathf.Clamp(bx, sMin, sMax);
            by = Mathf.Clamp(by, sMin, sMax);

            plan.DesiredWidth = Mathf.Max(1, Mathf.RoundToInt(island.PixelWidth * bx));
            plan.DesiredHeight = Mathf.Max(1, Mathf.RoundToInt(island.PixelHeight * by));
            plan.LastResult = bestResult;

            ATOLog.Trace($"[{usage.Texture.name}] island {island.PixelWidth}x{island.PixelHeight} -> " +
                         $"{plan.DesiredWidth}x{plan.DesiredHeight} (sx={bx:F3}, sy={by:F3}) {bestResult}");
        }

        private static bool IsSolid(LinearImage img)
        {
            if (img.Pixels.Length == 0) return true;
            var first = img.Pixels[0];
            for (int i = 1; i < img.Pixels.Length; i++)
            {
                if (math.any(math.abs(img.Pixels[i] - first) > 1e-4f)) return false;
            }
            return true;
        }

        /// <summary>
        /// EN: Bucket effect. Islands are shared by every texture of a UV stream, so the group's footprint
        ///     is simply the per-axis maximum of what its members asked for, clamped to the largest source
        ///     rect in the group (never upscale). Writing it onto the shared island is what guarantees that
        ///     the same UV lands at the same place in every parallel atlas.
        /// ZH: 木桶效应。岛由一条 UV 流上的所有贴图共享，因此组的占位就是各成员诉求的逐轴最大值，
        ///     并钳制到组内最大的源矩形（绝不放大）。把它写回共享的岛上，
        ///     正是“同一个 UV 在每一张平行图集上位置相同”的保证。
        /// </summary>
        public static void ResolveGroupFootprints(IEnumerable<IslandPlan> plans)
        {
            var byIsland = new Dictionary<UVIsland, List<IslandPlan>>();
            foreach (var plan in plans)
            {
                if (!byIsland.TryGetValue(plan.Island, out var list))
                    byIsland[plan.Island] = list = new List<IslandPlan>();
                list.Add(plan);
            }

            foreach (var kv in byIsland)
            {
                var island = kv.Key;
                int w = 0, h = 0, capW = 0, capH = 0;

                foreach (var plan in kv.Value)
                {
                    w = Mathf.Max(w, plan.DesiredWidth);
                    h = Mathf.Max(h, plan.DesiredHeight);
                    capW = Mathf.Max(capW, plan.SourceRect.width);
                    capH = Mathf.Max(capH, plan.SourceRect.height);
                }

                island.TargetWidth = Mathf.Clamp(w, 1, Mathf.Max(1, capW));
                island.TargetHeight = Mathf.Clamp(h, 1, Mathf.Max(1, capH));
            }
        }
    }
}
