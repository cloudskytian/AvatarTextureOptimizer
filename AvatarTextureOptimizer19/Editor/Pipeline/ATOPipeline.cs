// English: Bake orchestrator. Validates the component, runs every stage, reports, always disposes GPU/CPU memory.
// 中文：烘焙总控。校验组件、跑完全部分阶段、出报告，并始终释放 CPU/GPU/内存。
using System;
using System.Diagnostics;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;
using Net.Fosa.AvatarTextureOptimizer.API;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOPipeline
    {
        public static void Run(BuildContext context)
        {
            var root = context.AvatarRootObject;
            var comps = root.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (comps == null || comps.Length == 0) return;

            if (comps.Length > 1)
            {
                ErrorReport.ReportError(ATOLoc.L, ErrorSeverity.Error, "error.multiple");
                throw new InvalidOperationException("ATO: multiple components");
            }

            var comp = comps[0];
            if (comp.transform != root.transform || !comp.HasAvatarDescriptor)
            {
                ErrorReport.ReportError(ATOLoc.L, ErrorSeverity.Error, "error.noDescriptor");
                throw new InvalidOperationException("ATO: component must sit on VRCAvatarDescriptor");
            }

            ATOLoc.ApplyComponentLanguage(comp);
            var sw = Stopwatch.StartNew();
            using (var progress = new ATOProgress())
            using (var state = new ATOState())
            {
                state.Build = context;
                state.Component = comp;
                state.Anim = context.Extension<AnimatorServicesContext>();
                state.Log = new ATOLogger(comp.verboseLogging);
                state.Progress = progress;
                state.Platform = ResolvePlatform(comp);
                state.Settings = comp.ResolvePlatformSettings(state.Platform).Clone();
                state.Quality = comp.quality != null
                    ? comp.quality.Clone()
                    : ATOQualityParameters.FromPreset(comp.qualityPreset);
                state.Ext.AvatarRoot = root;
                state.Ext.Component = comp;
                state.Ext.Platform = state.Platform;
                state.Ext.PipelineState = state;
                state.Ext.Log = msg => state.Log.Info(msg);

                state.Log.Info("start avatar=" + root.name + " platform=" + state.Platform +
                               " preset=" + comp.qualityPreset + " atlas=" + state.Settings.generateAtlases);
                Hook(state, "start");

                try
                {
                    progress.Report("progress.validate", 0.02f);
                    progress.ThrowIfCanceled();

                    progress.Report("progress.scan", 0.08f);
                    using (state.Log.Time("whitelist")) ATOWhitelist.Collect(state);

                    progress.Report("progress.anim", 0.16f);
                    ATOAnimImpact anim;
                    using (state.Log.Time("animation")) anim = ATOAnimationAnalyzer.Analyze(state);

                    using (state.Log.Time("renderers")) ATORendererScanner.Scan(state, anim);
                    using (state.Log.Time("uses")) ATORendererScanner.CollectUses(state, anim);
                    Hook(state, "scanned");

                    progress.Report("progress.dedup", 0.28f);
                    using (state.Log.Time("texture-dedup")) ATOTextureDedup.Run(state);
                    using (state.Log.Time("eligibility")) ATOEligibility.Apply(state, anim);

                    progress.Report("progress.islands", 0.40f);
                    using (state.Log.Time("islands")) ATOIslandExtractor.Extract(state);
                    foreach (var proc in ATOExtensionRegistry.GetIslandProcessors())
                    {
                        try { proc.Process(state.Ext); }
                        catch (Exception e) { state.Log.Warn("island processor " + proc.Id + ": " + e.Message); }
                    }

                    progress.Report("progress.quality", 0.55f);
                    using (state.Log.Time("quality")) ATOQuality.ScaleIslands(state);

                    using (state.Log.Time("groups")) ATOGroups.Build(state);

                    progress.Report("progress.pack", 0.72f);
                    using (state.Log.Time("pack")) ATOPacker.Pack(state);

                    progress.Report("progress.apply", 0.86f);
                    using (state.Log.Time("uv-remap")) ATOUvRemapper.Apply(state);
                    using (state.Log.Time("materials")) ATOMaterialApply.ApplyTextures(state);

                    progress.Report("progress.matdedup", 0.93f);
                    using (state.Log.Time("dedup-final")) ATOMaterialApply.DedupAssets(state);

                    progress.Report("progress.report", 0.98f);
                    sw.Stop();
                    state.Report.TotalMs = sw.Elapsed.TotalMilliseconds;
                    state.Report.PushToNdmf();
                    state.Log.Info("done " + state.Report.Headline());
                    state.Log.VerboseInfo(state.Report.Details());
                    Hook(state, "done");
                }
                catch (ATOCanceledException)
                {
                    state.Report.Canceled = true;
                    state.Log.Warn("canceled by user; disk temps kept, memory released");
                    ErrorReport.ReportError(ATOLoc.L, ErrorSeverity.NonFatal, "progress.cancelled");
                }
                catch (Exception e)
                {
                    state.Log.Error("pipeline failed: " + e);
                    ErrorReport.ReportException(e);
                    throw;
                }
                finally
                {
                    // English: Always strip the component from the baked avatar.
                    // 中文：烘焙成品上必须移除自身组件。
                    if (comp != null) Object.DestroyImmediate(comp);
                    progress.Dispose();
                }
            }
        }

        private static void Hook(ATOState state, string stage)
        {
            foreach (var h in ATOExtensionRegistry.GetHooks())
            {
                try { h.OnStage(stage, state.Ext); }
                catch (Exception e) { state.Log.Warn("hook " + h.Id + ": " + e.Message); }
            }
        }

        internal static ATOBuildPlatform ResolvePlatform(AvatarTextureOptimizer comp)
        {
            if (comp.platformHint != ATOBuildPlatform.Auto) return comp.platformHint;
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android:
                    return ATOBuildPlatform.Android;
                case BuildTarget.iOS:
                    return ATOBuildPlatform.iOS;
                default:
                    return ATOBuildPlatform.PC;
            }
        }
    }
}
