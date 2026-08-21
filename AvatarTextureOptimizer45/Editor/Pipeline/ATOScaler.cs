using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;

namespace net.fosa.ato
{
    /// <summary>
    /// 质量缩放搜索 / Quality-driven scale search.
    ///
    /// 对每个岛:
    ///  * 目标质量=1(近无损) -> 跳过缩放, 原样拷贝(含纯色) / quality=1 -> skip scaling, copy as-is (incl. solid);
    ///  * 纯色岛短路 -> min(4, 原岛包围盒短边) / solid islands shortcut to min(4, bbox short side);
    ///  * 否则批量二分搜索: 全部岛并行评估(Burst 行并行作业 + GPU 全分辨率重采样), 先均匀后双轴细化
    ///    / otherwise batch bisection: all islands evaluated in parallel (Burst row-parallel jobs + GPU
    ///    full-res resampling), uniform first, then per-axis refinement (normals: uniform only);
    ///  * 像素密度钳制(px/m) 与 UV 组共享缩放(木桶效应) / texel-density clamp and UV-group shared scaling.
    ///
    /// 搜索主路径 = ATOBatchSearch(Burst+GPU); 异常时回退到 ATOQuality 的 CPU 参考实现.
    /// Primary path = ATOBatchSearch (Burst + GPU); falls back to the CPU reference in ATOQuality on failure.
    /// </summary>
    internal static class ATOScaler
    {
        private const float MinScale = 1f / 64f;

        public static void Run(ATOBuildState state)
        {
            Profiler.BeginSample("ATO.Scale");
            var timer = new ATOLog.StageTimer();
            timer.Start();
            var cfg = state.config;

            // ---------------------------------------------------------------
            // 1. 收集需要搜索的岛 / collect islands that need searching
            // ---------------------------------------------------------------
            var searches = new List<ATOIslandSearchData>();

            foreach (var tex in state.textures)
            {
                if (tex.skip == ATOSkip.Full || tex.dedupOf != null) continue;

                bool standalone = !cfg.enableAtlas || tex.skip == ATOSkip.AtlasOnly;
                tex.isStandaloneResult = standalone;

                var evalCtx = BuildEvalContext(state, tex);
                bool lossless = cfg.IsLosslessFor(tex.category);

                foreach (var island in tex.islands)
                {
                    if (!island.perTexture.TryGetValue(tex, out var it)) continue;
                    it.scale = Vector2.one;
                    it.resampleSkipped = false;

                    if (lossless)
                    {
                        it.scale = Vector2.one;
                        it.resampleSkipped = true;
                        it.targetWidth = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.width));
                        it.targetHeight = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.height));
                        continue;
                    }

                    var crop = ATOTextureIO.ReadRect(tex, it.pixelRect);
                    if (crop == null)
                    {
                        ATOLog.Warn($"岛采样失败, 保持原尺寸 / island sampling failed for {tex.source.name}; keeping original size");
                        it.targetWidth = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.width));
                        it.targetHeight = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.height));
                        continue;
                    }

                    int cropW = Mathf.Clamp(Mathf.CeilToInt(it.pixelRect.width), 1, Mathf.Max(1, tex.width));
                    int cropH = Mathf.Clamp(Mathf.CeilToInt(it.pixelRect.height), 1, Mathf.Max(1, tex.height));
                    if (crop.Length != cropW * cropH)
                    {
                        ATOLog.Warn($"岛裁剪尺寸不匹配 / crop size mismatch for {tex.source.name}; keeping original size");
                        it.targetWidth = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.width));
                        it.targetHeight = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.height));
                        continue;
                    }

                    searches.Add(new ATOIslandSearchData
                    {
                        island = island,
                        tex = tex,
                        it = it,
                        ctx = evalCtx,
                        cropW = cropW,
                        cropH = cropH,
                        crop = crop
                    });
                }
            }

            // ---------------------------------------------------------------
            // 2. 批量二分搜索(Burst+GPU; 异常回退CPU参考) / batch search
            // ---------------------------------------------------------------
            timer.BeginStep("batchSearch");
            try
            {
                ATOBatchSearch.Run(state, searches);
            }
            catch (Exception e)
            {
                ATOLog.Warn($"批量搜索失败, 回退CPU串行搜索 / batch search failed, falling back to CPU serial search: {e.Message}");
                foreach (var s in searches)
                {
                    FallbackSerialSearch(s, cfg);
                }
            }

            timer.EndStep();

            // ---------------------------------------------------------------
            // 3. 像素密度钳制 / texel-density clamp
            // ---------------------------------------------------------------
            timer.BeginStep("densityClamp");
            foreach (var tex in state.textures)
            {
                if (tex.skip == ATOSkip.Full || tex.dedupOf != null) continue;
                foreach (var island in tex.islands)
                {
                    if (!island.perTexture.TryGetValue(tex, out var it)) continue;
                    it.scale = ClampDensity(it.scale, island.worldArea, it.pixelRect.width * it.pixelRect.height, cfg);
                    it.targetWidth = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.width * it.scale.x));
                    it.targetHeight = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.height * it.scale.y));
                    it.densityScale = it.scale.x * it.scale.y;
                    it.individualScale = it.scale; // 共享前快照(图集收缩依据) / snapshot before sharing (atlas-shrink basis)
                }
            }

            timer.EndStep();

            // ---------------------------------------------------------------
            // 4. UV组共享缩放(木桶效应) / UV-group shared scaling (weakest link)
            // ---------------------------------------------------------------
            timer.BeginStep("uvGroupShare");
            foreach (var tex in state.textures)
            {
                if (tex.skip == ATOSkip.Full || tex.dedupOf != null || tex.isStandaloneResult) continue;
                foreach (var island in tex.islands)
                {
                    float sharedX = 0, sharedY = 0;
                    foreach (var t in island.textures)
                    {
                        if (t.isStandaloneResult) continue;
                        if (island.perTexture.TryGetValue(t, out var it))
                        {
                            sharedX = Mathf.Max(sharedX, it.scale.x);
                            sharedY = Mathf.Max(sharedY, it.scale.y);
                        }
                    }

                    if (sharedX <= 0 || sharedY <= 0) continue;
                    foreach (var t in island.textures)
                    {
                        if (t.isStandaloneResult) continue;
                        if (!island.perTexture.TryGetValue(t, out var it)) continue;
                        it.scale = new Vector2(sharedX, sharedY);
                        it.targetWidth = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.width * sharedX));
                        it.targetHeight = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.height * sharedY));
                    }
                }
            }

            timer.EndStep();

            // ---------------------------------------------------------------
            // 5. 独立贴图整图缩放 / whole-texture scale for standalone textures
            // ---------------------------------------------------------------
            foreach (var tex in state.textures)
            {
                if (tex.skip == ATOSkip.Full || tex.dedupOf != null) continue;
                if (!tex.isStandaloneResult) continue;
                float minAreaScale = 1f;
                foreach (var island in tex.islands)
                {
                    if (island.perTexture.TryGetValue(tex, out var it))
                    {
                        minAreaScale = Mathf.Min(minAreaScale, it.scale.x * it.scale.y);
                    }
                }

                tex.wholeScale = Mathf.Sqrt(minAreaScale);
                ATOLog.InfoVerbose($"整图缩放 / whole-texture scale: {tex.source.name} x{tex.wholeScale:F3}");
            }

            // 释放可读拷贝 / release readable copies
            foreach (var tex in state.textures)
            {
                ATOTextureIO.ReleaseReadable(tex);
            }

            timer.End("质量缩放 Quality Scaling");
            Profiler.EndSample();
        }

        // ------------------------------------------------------------------
        /// <summary>CPU 串行回退 / CPU serial fallback (reference implementation in ATOQuality).</summary>
        private static void FallbackSerialSearch(ATOIslandSearchData s, ATOConfig cfg)
        {
            var island = s.island;
            var tex = s.tex;
            var it = s.it;

            var sample = ATOIslandSample.Create(null, island, tex, it, s.ctx);
            if (sample == null)
            {
                it.scale = Vector2.one;
                it.targetWidth = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.width));
                it.targetHeight = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.height));
                return;
            }

            float lo = MinScale, hi = 1f;
            for (int i = 0; i < 14; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (sample.Evaluate(mid, mid).WorstRatio(cfg.quality) <= 1f) lo = mid;
                else hi = mid;
            }

            Vector2 scale = new Vector2(lo, lo);
            if (tex.category != ATOTextureCategory.Normal && lo > MinScale)
            {
                float sx = lo, sy = lo;
                // 单轴细化 / per-axis refinement
                float xlo = MinScale, xhi = lo;
                for (int i = 0; i < 12; i++)
                {
                    float mid = (xlo + xhi) * 0.5f;
                    if (sample.Evaluate(mid, sy).WorstRatio(cfg.quality) <= 1f) { sx = mid; xhi = mid; }
                    else xlo = mid;
                }

                float ylo = MinScale, yhi = lo;
                for (int i = 0; i < 12; i++)
                {
                    float mid = (ylo + yhi) * 0.5f;
                    if (sample.Evaluate(sx, mid).WorstRatio(cfg.quality) <= 1f) { sy = mid; yhi = mid; }
                    else ylo = mid;
                }

                scale = new Vector2(sx, sy);
            }

            it.scale = scale;
            it.targetWidth = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.width * scale.x));
            it.targetHeight = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.height * scale.y));
        }

        // ------------------------------------------------------------------
        private static ATOEvalContext BuildEvalContext(ATOBuildState state, ATOTextureInfo tex)
        {
            var ctx = new ATOEvalContext
            {
                category = tex.category,
                hasAlpha = tex.hasAlpha,
                usedChannels = tex.usedChannels
            };

            if (!ctx.hasAlpha) return ctx;

            var cutoffs = new List<float>();
            bool anyCutout = false, anyBlend = false;

            foreach (var r in tex.refs)
            {
                var mat = r.material;
                if (mat == null) continue;
                if (IsBlend(mat)) anyBlend = true;
                else if (IsCutout(mat))
                {
                    anyCutout = true;
                    if (mat.HasProperty("_Cutoff")) cutoffs.Add(mat.GetFloat("_Cutoff"));
                }

                if (state.anim.animatedCutoffs.TryGetValue(mat, out var animated))
                {
                    foreach (var v in animated)
                    {
                        cutoffs.Add(v);
                        anyCutout = true;
                    }
                }
            }

            foreach (var r in tex.refs)
            {
                if (r.material != null && state.anim.animatedRenderModeMaterials.Contains(r.material))
                {
                    ctx.renderModeAnimated = true;
                }
            }

            ctx.cutout = anyCutout;
            ctx.blend = anyBlend;
            if (cutoffs.Count == 0) cutoffs.Add(0.5f);
            ctx.cutoffs = cutoffs.ToArray();
            return ctx;
        }

        private static bool IsBlend(Material mat)
        {
            if (mat.renderQueue > 2500) return true;
            foreach (var kw in mat.shaderKeywords)
            {
                if (kw.Contains("ALPHABLEND") || kw.Contains("_FADE") || kw.Contains("_TRANSPARENT")) return true;
            }

            if (mat.HasProperty("_Mode"))
            {
                if (mat.GetFloat("_Mode") > 1.5f) return true;
            }

            return false;
        }

        private static bool IsCutout(Material mat)
        {
            foreach (var kw in mat.shaderKeywords)
            {
                if (kw.Contains("ALPHATEST") || kw.Contains("_CUTOUT")) return true;
            }

            if (mat.HasProperty("_Mode"))
            {
                float mode = mat.GetFloat("_Mode");
                if (mode > 0.5f && mode <= 1.5f) return true;
            }

            return false;
        }

        /// <summary>像素密度钳制 / texel-density clamp (area via sqrt(sx·sy)).</summary>
        private static Vector2 ClampDensity(Vector2 scale, float worldArea, float pixelArea, ATOConfig cfg)
        {
            if (worldArea <= 0 || pixelArea <= 0) return scale;
            float sLower = Mathf.Sqrt(worldArea * cfg.minDensity * cfg.minDensity / pixelArea);
            float sUpper = Mathf.Sqrt(worldArea * cfg.maxDensity * cfg.maxDensity / pixelArea);
            sLower = Mathf.Clamp(sLower, MinScale, 1f);
            sUpper = Mathf.Clamp(sUpper, MinScale, 1f);

            float area = scale.x * scale.y;
            float areaClamped = Mathf.Clamp(area, sLower * sLower, sUpper * sUpper);
            float ratio = Mathf.Sqrt(areaClamped / Mathf.Max(area, 1e-9f));
            return new Vector2(
                Mathf.Clamp(scale.x * ratio, MinScale, 1f),
                Mathf.Clamp(scale.y * ratio, MinScale, 1f));
        }
    }
}
