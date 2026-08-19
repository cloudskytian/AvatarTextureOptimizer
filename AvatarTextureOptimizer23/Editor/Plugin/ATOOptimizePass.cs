using System;
using System.Diagnostics;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Single orchestrating pass. Internal stages report progress and honor cancel.
    /// 单一编排 Pass。内部阶段汇报进度并响应取消。
    /// </summary>
    internal sealed class ATOOptimizePass : Pass<ATOOptimizePass>
    {
        public override string DisplayName => "ATO Optimize";

        protected override void Execute(BuildContext context)
        {
            var root = context.AvatarRootObject;
            var components = root.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (components == null || components.Length == 0)
            {
                Debug.Log($"{AvatarTextureOptimizer.LogPrefix} No component on avatar, skip.");
                return;
            }

            var sw = Stopwatch.StartNew();
            var ctx = context.GetState<ATOContext>();
            ctx.Build = context;
            ctx.Component = components[0];
            ATOLoc.SetMode(ctx.Component.language);

            var err = AvatarTextureOptimizer.ValidateMount(root, ctx.Component);
            if (err != null)
            {
                ATOLoc.Report(ErrorSeverity.Error, err);
                throw new InvalidOperationException(ATOLoc.T(err));
            }

            var platform = ATOPlatformUtil.Detect(context);
            ctx.Settings = ctx.Component.Resolve(platform);
            ctx.Log.Enabled = ctx.Settings.debugLog;
            ctx.Log.Info($"Start avatar='{root.name}' platform={platform} generateAtlas={ctx.Settings.generateAtlas} preset={ctx.Settings.qualityPreset}");

            ctx.TempFolder = ATOAssetUtil.EnsureTempFolder(context, root.name);
            ctx.Log.Detail($"Temp folder: {ctx.TempFolder}");

            using (ctx.Progress = new ATOProgress(ATOLoc.T("ato.progress.title")))
            {
                try
                {
                    RunStages(ctx);
                    sw.Stop();
                    ctx.Report.TotalMs = sw.Elapsed.TotalMilliseconds;
                    ATOReporter.Publish(ctx);
                    ctx.Log.Info($"Done in {ctx.Report.TotalMs:F0} ms");
                }
                catch (ATOCanceledException cex)
                {
                    ctx.Canceled = true;
                    ctx.Log.Warn($"Canceled at '{cex.Stage}'. Temp assets kept on disk.");
                    ATOLoc.Report(ErrorSeverity.Information, "ato.info.canceled", cex.Stage);
                    throw;
                }
                catch (Exception e)
                {
                    ctx.Log.Error(e.ToString());
                    throw;
                }
                finally
                {
                    ctx.Dispose();
                }
            }
        }

        private static void RunStages(ATOContext ctx)
        {
            ctx.Progress.Report(0.02f, ATOLoc.T("ato.progress.scan"));
            using (ctx.Log.Stage("ScanAvatar"))
                ATOAvatarScanner.Run(ctx);

            ctx.Progress.Report(0.10f, ATOLoc.T("ato.progress.shader"));
            using (ctx.Log.Stage("AnalyzeShaders"))
                ATOShaderHub.AnalyzeAll(ctx);

            ctx.Progress.Report(0.18f, ATOLoc.T("ato.progress.anim"));
            using (ctx.Log.Stage("AnalyzeAnimation"))
                ATOAnimationAnalyzer.Run(ctx);

            ctx.Progress.Report(0.24f, ATOLoc.T("ato.progress.whitelist"));
            using (ctx.Log.Stage("ResolveWhitelist"))
                ATOWhitelist.Run(ctx);

            ctx.Progress.Report(0.30f, ATOLoc.T("ato.progress.dedup_tex"));
            using (ctx.Log.Stage("DedupTextures"))
                ATOTextureDedup.Run(ctx);

            ctx.Progress.Report(0.40f, ATOLoc.T("ato.progress.islands"));
            using (ctx.Log.Stage("ExtractIslands"))
                ATOIslandExtractor.Run(ctx);

            ctx.Progress.Report(0.52f, ATOLoc.T("ato.progress.quality"));
            using (ctx.Log.Stage("QualityScale"))
                ATOQualityScaler.Run(ctx);

            ctx.Progress.Report(0.62f, ATOLoc.T("ato.progress.groups"));
            using (ctx.Log.Stage("BuildGroups"))
                ATOGroupBuilder.Run(ctx);

            if (ctx.Settings.generateAtlas)
            {
                ctx.Progress.Report(0.72f, ATOLoc.T("ato.progress.pack"));
                using (ctx.Log.Stage("PackAtlases"))
                    ATOAtlasPipeline.Run(ctx);
            }
            else
            {
                ctx.Progress.Report(0.72f, ATOLoc.T("ato.progress.scale_whole"));
                using (ctx.Log.Stage("ScaleWholeTextures"))
                    ATOWholeTextureScaler.Run(ctx);
            }

            ctx.Progress.Report(0.84f, ATOLoc.T("ato.progress.apply"));
            using (ctx.Log.Stage("Apply"))
                ATOApply.Run(ctx);

            ctx.Progress.Report(0.92f, ATOLoc.T("ato.progress.dedup_mat"));
            using (ctx.Log.Stage("DedupMaterials"))
                ATOMaterialDedup.Run(ctx);

            ctx.Progress.Report(0.96f, ATOLoc.T("ato.progress.import"));
            using (ctx.Log.Stage("ImportSettings"))
                ATOImportSettings.Run(ctx);

            ctx.Progress.Report(0.98f, ATOLoc.T("ato.progress.aao"));
            using (ctx.Log.Stage("AAOCompatibility"))
                ATOAaoCompat.EvacuateIfNeeded(ctx);

            ctx.Progress.Report(1f, ATOLoc.T("ato.progress.done"));
        }
    }

    /// <summary>
    /// Removes the component from the finished avatar. NDMF already treats INDMFEditorOnly as editor-only,
    /// but we still destroy explicitly so the upload avatar is clean.
    /// 从成品 Avatar 上移除自身。NDMF 已把 INDMFEditorOnly 当编辑器组件，这里仍显式销毁以保证上传干净。
    /// </summary>
    internal sealed class ATOCleanupPass : Pass<ATOCleanupPass>
    {
        public override string DisplayName => "ATO Cleanup";

        protected override void Execute(BuildContext context)
        {
            var list = context.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            foreach (var c in list)
            {
                if (c != null) UnityEngine.Object.DestroyImmediate(c);
            }
        }
    }
}
