// ============================================================================
// ATOPipeline.cs — 优化管线编排 / Optimization pipeline orchestration
// (EN) Sequentially runs the ATO pipeline stages. Each stage is implemented in
//      its own module; this file defines the order and progress reporting.
// (ZH) 顺序运行 ATO 管线各阶段。每阶段独立成模块，本文件定义顺序与进度报告。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Fosa.AvatarTextureOptimizer
{
    public class ATOPipeline
    {
        private readonly ATOBuildContext _ctx;

        // 各阶段收集的中间结果 / intermediate results shared between stages
        public readonly List<object> Stages = new List<object>();

        public ATOPipeline(ATOBuildContext ctx) => _ctx = ctx;

        public void Run()
        {
            var stages = new (string key, Action action)[]
            {
                ("ato.phase.collect", StageCollect),
                ("ato.phase.animations", StageAnalyzeAnimations),
                ("ato.phase.dedup", StageDedupTextures),
                ("ato.phase.uv", StageExtractIslands),
                ("ato.phase.quality", StageQualityScale),
                ("ato.phase.pack", StagePackAtlases),
                ("ato.phase.apply", StageApply),
                ("ato.phase.report", StageReport),
            };

            for (int i = 0; i < stages.Length; i++)
            {
                var (key, action) = stages[i];
                using (var progress = ATOProgress.Stage(ATOLocalization.T(_ctx.Language, key), i, stages.Length))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    ATOLog.Info(ATOLocalization.T(_ctx.Language, key) + " ...");
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        ATOLog.Error($"Stage '{key}' failed: {e}");
                        throw;
                    }
                    finally
                    {
                        sw.Stop();
                        _ctx.Report.AddStep(ATOLocalization.T(_ctx.Language, key), sw.Elapsed.TotalMilliseconds);
                    }
                    progress.Report(1f);
                }
            }
        }

        // ---- 阶段实现 / stage implementations ----

        private void StageCollect()
        {
            var stage = new CollectStage(_ctx);
            stage.Run();
            _ctx.Collect = stage.Result;
        }

        private void StageAnalyzeAnimations()
        {
            var stage = new AnimationStage(_ctx, _ctx.Collect);
            stage.Run();
        }

        private void StageDedupTextures()
        {
            // 贴图去重已在 Collect 阶段随注册完成（内容+设置相同 → 规范引用）。
            // 引用的实际更新（material.SetTexture / 动画曲线）在 Apply 阶段统一执行。
            // Texture dedup is performed at registration time in Collect; actual
            // reference rewriting happens in Apply.
            ATOLog.VerboseLog($"[dedup] {_ctx.Collect.DedupPairs.Count} duplicate texture pairs found");
        }

        private void StageExtractIslands()
        {
            var stage = new IslandStage(_ctx, _ctx.Collect);
            stage.Run();
            _ctx.Islands = stage.Result;
        }

        private void StageQualityScale()
        {
            var stage = new QualityStage(_ctx, _ctx.Islands);
            stage.Run();
        }

        private void StagePackAtlases()
        {
            var stage = new PackStage(_ctx, _ctx.Islands);
            stage.Run();
            _ctx.Pack = stage.Result;
        }

        private void StageApply()
        {
            var stage = new ApplyStage(_ctx);
            stage.Run();
        }

        private void StageReport()
        {
            // 填充报告 / fill report
            var r = _ctx.Report;
            r.texturesProcessed = _ctx.Collect.Canonical.Count;
            r.atlasesGenerated = _ctx.Pack.Atlases.Count;
            foreach (var atlas in _ctx.Pack.Atlases)
            {
                r.approxBytesBefore += (long)atlas.Width * atlas.Height * 4;
                r.approxBytesAfter += (long)atlas.Width * atlas.Height * 4;
                var entry = new ATOReport.AtlasEntry
                {
                    name = atlas.Name,
                    width = atlas.Width,
                    height = atlas.Height,
                    utilization = ComputeUtilization(atlas),
                };
                foreach (var g in atlas.Groups)
                    foreach (var t in g.Textures)
                        if (t.Texture != null && !entry.sourceTextures.Contains(t.Texture.name))
                            entry.sourceTextures.Add(t.Texture.name);
                entry.islandCount = CountIslands(atlas);
                r.atlases.Add(entry);
            }
        }

        private float ComputeUtilization(ATOAtlas atlas)
        {
            long used = 0;
            foreach (var g in atlas.Groups)
                foreach (var i in g.Islands)
                    if (i.RasterizedMask != null)
                        used += (long)i.ScaledPixelW * i.ScaledPixelH;
            long total = (long)atlas.Width * atlas.Height;
            return total == 0 ? 0f : (float)used / total;
        }

        private int CountIslands(ATOAtlas atlas)
        {
            int n = 0;
            foreach (var g in atlas.Groups)
                foreach (var i in g.Islands)
                    if (i.RasterizedMask != null) n++;
            return n;
        }
    }
}
