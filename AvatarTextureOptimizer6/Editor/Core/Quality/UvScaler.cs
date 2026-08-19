using System;
using System.Collections.Generic;
using NetFosa.AvatarTextureOptimizer.Editor.Analysis;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using NetFosa.AvatarTextureOptimizer.Editor.UV;
using UnityEngine;
using NetFosa.AvatarTextureOptimizer;

namespace NetFosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// UV 缩放器：对每个 UV 岛、每个组内贴图做质量二分搜索（先均匀，后双轴独立细化），
    /// 并施加像素密度上下限（min/max px/m，含形态键与动画缩放的世界面积）。
    /// 组级取各贴图缩放的最大值（木桶效应取最大尺寸，≤组内最大原尺寸）。
    /// 纯色岛短路缩到 min(4, 原岛包围盒短边)；目标质量为 1 的类型直接跳过缩放。
    /// </summary>
    public sealed class UvScaler
    {
        private readonly QualityEvaluator _evaluator;
        private readonly EffectiveSettings _settings;
        private readonly AnimationAnalysis _animation;
        private readonly ATOLogger _logger;
        private readonly BuildReport _report;

        public UvScaler(QualityEvaluator evaluator, EffectiveSettings settings, AnimationAnalysis animation,
            ATOLogger logger, BuildReport report)
        {
            _evaluator = evaluator;
            _settings = settings;
            _animation = animation;
            _logger = logger;
            _report = report;
        }

        public void ScaleAll(List<UvGroup> groups)
        {
            foreach (var group in groups)
            {
                if (group.failed) continue;
                // noAtlas 组也要算岛缩放（整图缩放用）；只是不参与图集化
                ScaleGroup(group);
            }
        }

        private void ScaleGroup(UvGroup group)
        {
            if (group.islands == null || group.islands.Count == 0) return;

            foreach (var island in group.islands)
            {
                if (island.failed) continue;

                // 世界面积（含形态键/动画缩放），密度用
                island.worldAreaM2 = WorldAreaCalculator.ComputeIslandAreaM2(island, _animation);

                float groupSu = 1f, groupSv = 1f;
                bool allPure = true;
                bool anyScaled = false;

                foreach (var gt in group.textures)
                {
                    var info = gt.info;
                    if (info == null || info.texture == null) continue;
                    if (info.dedupTarget != null) continue;
                    if (info.EffectiveWhitelistLevel == ATOWhitelistLevel.Full)
                    {
                        // 完全白名单：不缩放
                        allPure = false;
                        continue;
                    }
                    if (_settings.quality.IsNearLossless)
                    {
                        // 目标质量为 1 → 跳过该类型缩放
                        allPure = false;
                        continue;
                    }

                    var tex = info.texture;
                    int texW = tex.width, texH = tex.height;

                    var (su, sv) = ScaleIslandForTexture(info, gt, island, texW, texH);
                    anyScaled = true;
                    groupSu = Mathf.Max(groupSu, su);
                    groupSv = Mathf.Max(groupSv, sv);

                    // 纯色判断（本贴图）
                    bool pure = _evaluator.IsRegionUniform(info, island, texW, texH);
                    if (!pure) allPure = false;
                }

                if (!anyScaled && _settings.quality.IsNearLossless)
                {
                    groupSu = 1f; groupSv = 1f;
                }

                // 纯色标记（各贴图已按像素短边短路缩放，组级取最大即可）
                if (allPure && !_settings.quality.IsNearLossless)
                {
                    island.pureColor = true;
                    _logger.VerboseLog($"Island {island.id} in group {group.id}: pure color (all textures).");
                    _report.InfoLines.Add($"pure-color island #{island.id} (group {group.id})");
                }

                island.scaleU = Mathf.Clamp01(groupSu);
                island.scaleV = Mathf.Clamp01(groupSv);
            }
        }

        /// <summary>对单个 (贴图, 岛) 做缩放搜索，返回 (su, sv)。</summary>
        private (float, float) ScaleIslandForTexture(TextureInfo info, UvGroupTexture gt, UvIsland island,
            int texW, int texH)
        {
            var bounds = island.uvBounds;
            int pw = Mathf.Max(1, Mathf.RoundToInt(bounds.width * texW));
            int ph = Mathf.Max(1, Mathf.RoundToInt(bounds.height * texH));
            int shortSide = Mathf.Min(pw, ph);

            // 纯色短路
            if (_evaluator.IsRegionUniform(info, island, texW, texH))
            {
                float target = Mathf.Min(4f, shortSide);
                float s = Mathf.Clamp01(target / Mathf.Max(shortSide, 1));
                return (s, s);
            }

            // 密度上下限
            float scaleLower = 0f, scaleUpper = 1f;
            if (island.worldAreaM2 > 1e-8f)
            {
                float origDensity = Mathf.Sqrt((pw * (float)ph) / island.worldAreaM2); // px/m（几何平均）
                scaleUpper = Mathf.Clamp01(_settings.maxPixelsPerMeter / Mathf.Max(origDensity, 1e-6f));
                scaleLower = Mathf.Clamp01(_settings.minPixelsPerMeter / Mathf.Max(origDensity, 1e-6f));
                if (scaleLower > scaleUpper) scaleLower = scaleUpper;
            }

            var thresholds = _settings.quality;

            // ---- 均匀二分搜索（找最大通过缩放） ----
            float lo = scaleLower, hi = scaleUpper;
            bool loPass = EvaluatePass(info, gt, island, texW, texH, lo, lo);
            if (!loPass)
            {
                // 密度下限都无法达标：接受下限并警告
                _logger.Warn($"Island {island.id} ({info.texture?.name}) fails quality even at density floor {lo:P0}; using floor.");
                return (lo, lo);
            }

            for (int iter = 0; iter < 10; iter++)
            {
                float mid = (lo + hi) * 0.5f;
                if (EvaluatePass(info, gt, island, texW, texH, mid, mid)) lo = mid;
                else hi = mid;
            }
            float su = lo, sv = lo;

            // ---- 双轴独立细化（在保证通过的前提下进一步缩小单轴） ----
            float refineMin = Mathf.Max(scaleLower, su * 0.25f);

            // 细化 U（固定 V）
            {
                float rlo = refineMin, rhi = su;
                bool rloPass = EvaluatePass(info, gt, island, texW, texH, rlo, su);
                if (rloPass)
                {
                    for (int iter = 0; iter < 8; iter++)
                    {
                        float mid = (rlo + rhi) * 0.5f;
                        if (EvaluatePass(info, gt, island, texW, texH, mid, su)) rhi = mid;
                        else rlo = mid;
                    }
                    su = rhi;
                }
            }
            // 细化 V（固定 U）
            {
                float rlo = refineMin, rhi = sv;
                bool rloPass = EvaluatePass(info, gt, island, texW, texH, su, rlo);
                if (rloPass)
                {
                    for (int iter = 0; iter < 8; iter++)
                    {
                        float mid = (rlo + rhi) * 0.5f;
                        if (EvaluatePass(info, gt, island, texW, texH, su, mid)) rhi = mid;
                        else rlo = mid;
                    }
                    sv = rhi;
                }
            }

            // 密度下界兜底（细化可能把面积缩到低于 min 密度，按比例回升）
            if (island.worldAreaM2 > 1e-8f)
            {
                float finalPx = (pw * su) * (ph * sv);
                float finalDensity = Mathf.Sqrt(finalPx / island.worldAreaM2);
                if (finalDensity < _settings.minPixelsPerMeter)
                {
                    float k = Mathf.Clamp01(Mathf.Sqrt((float)_settings.minPixelsPerMeter / Mathf.Max(finalDensity, 1e-6f)));
                    su = Mathf.Min(1f, su * k);
                    sv = Mathf.Min(1f, sv * k);
                }
            }

            return (su, sv);
        }

        private bool EvaluatePass(TextureInfo info, UvGroupTexture gt, UvIsland island, int texW, int texH,
            float su, float sv)
        {
            if (su <= 0f || sv <= 0f) return false;
            var r = _evaluator.Evaluate(info, gt, island, texW, texH, su, sv, _settings.quality);
            return r.pass;
        }
    }
}
