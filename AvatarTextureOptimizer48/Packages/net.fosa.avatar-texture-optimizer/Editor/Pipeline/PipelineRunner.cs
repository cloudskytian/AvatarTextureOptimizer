// Pipeline runner: orchestrates analysis -> scaling -> packing -> baking -> write-back,
// with progress, cancellation, timing and the final report.
// / 流水线运行器：协调 分析 -> 缩放 -> 装箱 -> 烘焙 -> 回写，含进度、取消、耗时与最终报告。

using System;
using UnityEditor;
using UnityEngine;
using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.editor.analysis;
using net.fosa.avatar_texture_optimizer.editor.baking;
using net.fosa.avatar_texture_optimizer.editor.packing;
using net.fosa.avatar_texture_optimizer.editor.quality;
using net.fosa.avatar_texture_optimizer.editor.writeback;
using net.fosa.avatar_texture_optimizer.runtime;

namespace net.fosa.avatar_texture_optimizer.editor.pipeline
{
    /// <summary>
    /// Runs the whole ATO pipeline. / 运行整个 ATO 流水线。
    /// </summary>
    public static class PipelineRunner
    {
        /// <summary>Entry point invoked by the NDMF pass. / NDMF Pass 调用的入口。</summary>
        public static void Run(BuildContext ctx, AvatarTextureOptimizer component)
        {
            AtoLog.Verbose = component.verboseLogs;
            var report = new BuildReport();

            // Determine platform / 确定平台
            BuildTargetHint hint;
            bool mobile;
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android:
                    hint = BuildTargetHint.Android; mobile = true; break;
                case BuildTarget.iOS:
                    hint = BuildTargetHint.iOS; mobile = true; break;
                default:
                    hint = BuildTargetHint.Standalone; mobile = false; break;
            }

            try
            {
                using (var progress = new ProgressScope("Avatar Texture Optimizer / 贴图优化"))
                {
                    AnalysisResult analysis = null;
                    PackingResult packing = null;

                    report.AddStage("Analyze / 分析", stat =>
                    {
                        progress.Report("Analyzing / 分析中", "", 0.05f);
                        analysis = AvatarAnalyzer.Analyze(ctx.AvatarRootObject.transform, component);
                        stat.Notes.Add(analysis.Meshes.Count + " renderers / 渲染器");
                        stat.Notes.Add(analysis.Textures.Count + " textures (deduped) / 贴图（去重后）");
                        stat.Notes.Add(analysis.UvGroups.Count + " UV groups / UV 组");
                    });

                    report.AddStage("Scale islands / 缩放 UV 岛", stat =>
                    {
                        IslandScaler.ComputeScales(analysis, component, progress);
                        int n = 0;
                        foreach (var g in analysis.UvGroups) n += g.Islands.Count;
                        report.ProcessedIslands = n;
                        stat.Notes.Add(n + " islands / 岛");
                    });

                    report.AddStage("Pack / 装箱", stat =>
                    {
                        progress.Report("Packing / 装箱中", "", 0.62f);
                        packing = PackingPlanner.Plan(analysis, component, hint, mobile, progress);
                        stat.Notes.Add(packing.Atlases.Count + " atlases planned / 计划图集");
                        stat.Notes.Add("layout size " + packing.LayoutSize);
                    });

                    report.AddStage("Bake / 烘焙", stat =>
                    {
                        AtlasBaker.BakeAll(ctx, packing, component, hint, mobile, progress, report);
                        stat.Notes.Add(report.Atlases.Count + " atlases baked / 图集已烘焙");
                    });

                    report.AddStage("Write back / 回写", stat =>
                    {
                        WriteBackProcessor.SetLayoutSize(packing.LayoutSize);
                        WriteBackProcessor.Process(ctx, analysis, packing, component, progress, report);
                    });

                    // Report totals / 汇总
                    long src = 0;
                    int processed = 0;
                    foreach (var r in analysis.Textures)
                    {
                        if (r.Whitelisted) continue;
                        src += (long)r.Width * r.Height;
                        processed++;
                    }
                    report.SourceTotalTexels = src;
                    report.ProcessedTextures = processed;
                    report.SkippedAsWhitelist = analysis.WhitelistedTextureCount;
                    foreach (var w in analysis.Warnings)
                    {
                        report.WarningMessages.Add(w);
                        AtoLog.Warn(w);
                    }

                    long outTex = 0;
                    foreach (var a in report.Atlases) outTex += (long)a.Width * a.Height;
                    report.OutputTotalTexels = outTex;
                }

                PrintReport(report, component);
            }
            catch (OperationCanceledException e)
            {
                report.Cancelled = true;
                report.ErrorMessage = e.Message;
                AtoLog.Warn(report.ErrorMessage);
            }
            catch (Exception e)
            {
                report.ErrorMessage = e.Message;
                AtoLog.Error("[ATO] Build failed: " + e);
            }
            finally
            {
                TextureReader.ClearCache();
            }
        }

        private static void PrintReport(BuildReport report, AvatarTextureOptimizer component)
        {
            var text = report.Render(component.verboseLogs);
            Debug.Log(text);
        }
    }
}
