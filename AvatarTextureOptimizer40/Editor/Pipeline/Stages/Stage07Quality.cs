using System.Collections.Generic;
using Fosa.Ato.Editor.Quality;
using Fosa.Ato.Editor.Util;
using UnityEngine;

namespace Fosa.Ato.Editor.Pipeline.Stages
{
    /// <summary>
    /// Stage 07: For each UV group (islands that share a UV identity across maps), binary-search the
    /// smallest uniform scale that passes every map's quality thresholds; then refine anisotropically.
    /// Clamp by min/max pixel density and the island's true source pixel size. Quality==1 skips
    /// resampling entirely (raw copy); solid-color islands collapse to min(4, short edge).
    /// 阶段 07：对每个 UV 组二分搜索满足所有贴图质量阈值的最小均匀缩放，再做各向异性细化；受最小/最大
    /// 像素密度与源真实像素尺寸钳制。质量==1 完全跳过重采样（原样拷贝）；纯色岛缩到 min(4,短边)。
    /// </summary>
    internal sealed class Stage07Quality : IStage
    {
        public string Name => "ATO/07 Quality scaling";
        public float Weight => 8f;

        public void Run(AtoPipeline p)
        {
            if (!p.Settings.GenerateAtlas)
            {
                // No atlas: whole-texture scaling is handled in stage 09; just build 1:1 groups.
                // 不生成图集：整图缩放在阶段09处理，这里仅建 1:1 组
                BuildPassthroughGroups(p);
                return;
            }

            // Group islands by UV identity: (mesh, channel, submesh, UvBox approximately).
            // 按 UV 身份分组：（网格，通道，子网格，UvBox 近似）
            var groups = new Dictionary<int, UvGroup>();
            int gid = 0;
            foreach (var isl in p.Islands)
            {
                p.Progress.ThrowIfCancelled();
                int key = (isl.Uv.Mesh?.GetInstanceID() ?? 0) * 397 ^ isl.Uv.Channel * 17 ^ isl.Uv.SubMesh;
                // Box quantized to 1/4096 to group same-identity overlaps across maps.
                key = key * 397 ^ Quant(isl.UvBox.xMin) ^ (Quant(isl.UvBox.yMin) << 4) ^ (Quant(isl.UvBox.width) << 8);
                if (!groups.TryGetValue(key, out var g))
                {
                    g = new UvGroup { Id = gid++ };
                    groups[key] = g; p.UvGroups.Add(g);
                }
                g.Islands.Add(isl);
                if (isl.SourceUsage != null && !g.Maps.Contains(isl.SourceUsage)) g.Maps.Add(isl.SourceUsage);
            }

            var evaluator = new QualityEvaluator(p);
            foreach (var g in p.UvGroups)
            {
                p.Progress.ThrowIfCancelled();
                ProcessGroup(p, g, evaluator);
            }
            AtoLog.VIf(p.Settings.VerboseLogging, $"Scaled {p.UvGroups.Count} UV group(s).");
        }

        private static int Quant(float f) => Mathf.RoundToInt(f * 4096f);

        private static void ProcessGroup(AtoPipeline p, UvGroup g, QualityEvaluator ev)
        {
            // Wood bucket: group size must satisfy every map; cap at largest original short edge.
            // 木桶效应：尺寸需满足每张贴图；上限为组内最大原短边
            float maxOrigShort = 0f;
            foreach (var isl in g.Islands)
                maxOrigShort = Mathf.Max(maxOrigShort, Mathf.Min(isl.SizePx.x, isl.SizePx.y));
            g.MaxOriginalSizePx = new Vector2(maxOrigShort * 2, maxOrigShort * 2);

            // Density clamp: world area -> target pixels. / 密度钳制：世界面积->目标像素
            float worldShortM = Mathf.Sqrt(g.Islands[0].WorldArea);
            float densityMin = p.Settings.MinPixelDensity;
            float densityMax = p.Settings.MaxPixelDensity;
            float densityPx = Mathf.Clamp(worldShortM * densityMax, worldShortM * densityMin, maxOrigShort);

            // Determine if near-lossless (all params effectively 1) -> raw copy.
            // 近无损（参数都为1）-> 原样拷贝
            bool nearLossless = true;
            foreach (var m in g.Maps)
            {
                var cls = p.Settings.GetClass(m.Kind, m.HasAlphaChannel);
                if (cls.MsSsim < 0.9995f) nearLossless = false;
            }

            if (nearLossless)
            {
                g.BucketSizePx = new Vector2(maxOrigShort, maxOrigShort);
                foreach (var isl in g.Islands)
                {
                    isl.TargetSizePx = isl.SizePx;
                    isl.SolidColor = false;
                }
                return;
            }

            // Binary search uniform scale from 0.125..1.0; worst-metric passes.
            // 二分均匀缩放 0.125..1.0；最差指标需达标
            float lo = 0.125f, hi = 1.0f;
            for (int it = 0; it < 6; it++)
            {
                float mid = (lo + hi) * 0.5f;
                if (ev.GroupPassesAt(g, mid)) hi = mid; else lo = mid;
            }
            float uniform = Mathf.Clamp(hi, 0.125f, 1.0f);

            // Anisotropic refinement per axis (two independent bisections). / 各向异性细化
            float sx = uniform, sy = uniform;
            for (int it = 0; it < 4; it++)
            {
                float m = (sx + 1f) * 0.5f;
                if (ev.GroupPassesAt(g, m, sy)) sx = m; else break;
            }
            for (int it = 0; it < 4; it++)
            {
                float m = (sy + 1f) * 0.5f;
                if (ev.GroupPassesAt(g, sx, m)) sy = m; else break;
            }
            sx = Mathf.Min(sx, 1f); sy = Mathf.Min(sy, 1f);

            foreach (var isl in g.Islands)
            {
                var shortPx = Mathf.Min(isl.SizePx.x, isl.SizePx.y);
                float targetShort = Mathf.Min(shortPx, Mathf.Max(2f, shortPx * Mathf.Min(sx, sy)));
                // Solid color short circuit / 纯色短路
                if (ev.IsSolid(isl))
                {
                    isl.SolidColor = true;
                    targetShort = Mathf.Min(4f, shortPx);
                }
                targetShort = Mathf.Clamp(targetShort, 1f, Mathf.Min(shortPx, densityPx));
                float ratio = targetShort / Mathf.Max(1f, shortPx);
                isl.TargetSizePx = new Vector2(
                    Mathf.Max(1f, Mathf.Round(isl.SizePx.x * ratio)),
                    Mathf.Max(1f, Mathf.Round(isl.SizePx.y * ratio)));
            }

            g.BucketSizePx = new Vector2(
                Mathf.Max(1f, Mathf.Round(maxOrigShort * Mathf.Max(sx, sy))),
                Mathf.Max(1f, Mathf.Round(maxOrigShort * Mathf.Max(sx, sy))));
            if (g.BucketSizePx.x > g.MaxOriginalSizePx.x) g.BucketSizePx = g.MaxOriginalSizePx;
        }

        private static void BuildPassthroughGroups(AtoPipeline p)
        {
            foreach (var isl in p.Islands)
            {
                var g = new UvGroup();
                g.Islands.Add(isl);
                if (isl.SourceUsage != null) g.Maps.Add(isl.SourceUsage);
                isl.TargetSizePx = isl.SizePx;
                g.BucketSizePx = isl.SizePx;
                p.UvGroups.Add(g);
            }
        }
    }
}
