// ATOProcessor.cs
// Build pipeline orchestrator shared by the three NDMF passes.
// 三个 NDMF Pass 共用的构建管线编排器。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>Per-avatar processing state machine. / 单 Avatar 处理状态机。</summary>
    internal sealed partial class ATOProcessor
    {
        private readonly ATOBuildData _d = new ATOBuildData();
        private ATOProgress _progress;

        // ------------------------------------------------------------------ //
        // Stage 1: Analyze / 阶段1:分析
        // ------------------------------------------------------------------ //
        internal static void RunAnalyze(BuildContext ctx)
        {
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            ATOProcessor p = null;
            try
            {
                p = new ATOProcessor { _d = { Ctx = ctx } };
                p.ValidateComponent();
                if (p._d.Component == null) return; // already reported / 已报告

                ATOLog.ResetTimings();
                ATOLog.EnableVerbose(p._d.Component.verboseLogging);
                ATOLog.Info($"Avatar Texture Optimizer v{Version.String} — analyze start on '{ctx.AvatarRootObject.name}'");

                using (var prog = new ATOProgress("ATO: Analyze"))
                {
                    p._progress = prog;
                    p.ResolvePlatform();
                    p.CollectAnimations();
                    p.CollectRenderers();
                    p.AnalyzeMaterials();
                    p.BuildWhitelist();
                    p.DedupeTextures();
                    p.BuildUsageGraph();
                    p.ExtractIslands();
                }
                _last = p;
            }
            catch (ATOCancelledException)
            {
                ATOCleanup.OnCancelled();
                throw;
            }
            catch (Exception e)
            {
                ReportFatal(e, "analyze");
                throw;
            }
            finally
            {
                swTotal.Stop();
                ATOLog.V($"analyze total: {swTotal.Elapsed.TotalMilliseconds:F1} ms");
            }
        }

        // ------------------------------------------------------------------ //
        // Stage 2: Optimize (quality→pack→bake) / 阶段2:优化(质量→装箱→烘焙)
        // ------------------------------------------------------------------ //
        internal static void RunOptimize(BuildContext ctx)
        {
            var p = _last;
            if (p?._d.Component == null) return; // no component or invalid mount → skip / 无组件→跳过
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var prog = new ATOProgress("ATO: Optimize Textures"))
                {
                    p._progress = prog;
                    p.ScaleIslands();
                    if (p._d.EffectiveProfile.generateAtlas)
                    {
                        p.PackAtlases();
                        p.BakeAtlases();
                        p.FillWholeTexScales(onlyFallback: true);
                        p.BakeStandaloneTextures();
                    }
                    else
                    {
                        p.FillWholeTexScales(onlyFallback: false);
                        p.BakeStandaloneTextures();
                    }
                    p.RewriteReferences();
                    p.ApplyCompression();
                }
            }
            catch (ATOCancelledException)
            {
                ATOCleanup.OnCancelled();
                throw;
            }
            catch (Exception e)
            {
                ReportFatal(e, "optimize");
                throw;
            }
            finally
            {
                swTotal.Stop();
                ATOLog.V($"optimize total: {swTotal.Elapsed.TotalMilliseconds:F1} ms");
                _last = p;
            }
        }

        // ------------------------------------------------------------------ //
        // Stage 3: Finalize / 阶段3:收尾
        // ------------------------------------------------------------------ //
        internal static void RunFinalize(BuildContext ctx)
        {
            var p = _last;
            if (p?._d.Component == null) return;
            try
            {
                using (var prog = new ATOProgress("ATO: Finalize"))
                {
                    p._progress = prog;
                    p.DedupeMaterialsAndSlots();
                    p.RegisterAaoCompatibility();
                    p.WriteReport();
                    p.RemoveComponent();
                }
            }
            catch (ATOCancelledException)
            {
                ATOCleanup.OnCancelled();
                throw;
            }
            catch (Exception e)
            {
                ReportFatal(e, "finalize");
                throw;
            }
            finally
            {
                ATOCleanup.OnBuildEnd(_last);
                _last = null;
            }
        }

        /// <summary>Processor alive across passes (NDMF runs passes sequentially on one avatar). / 跨 Pass 存活的处理器实例。</summary>
        private static ATOProcessor _last;

        internal static ATOProcessor Current => _last;

        // ------------------------------------------------------------------ //
        // Fatal error helper / 致命错误辅助
        // ------------------------------------------------------------------ //
        private static void ReportFatal(Exception e, string stage)
        {
            ATOLog.Error($"internal error in '{stage}': {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            ErrorReport.ReportException(e);
        }

        /// <summary>Progress tick with cancel. / 进度心跳(可取消)。</summary>
        private void Tick(string info, float t) => _progress?.Report(info, t);

        private void TickIndeterminate(string info) => _progress?.ReportIndeterminate(info);
    }

    /// <summary>Package version string. / 包版本号。</summary>
    internal static class Version
    {
        internal const string String = "0.1.0";
    }
}
