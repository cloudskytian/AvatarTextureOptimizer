// Pipeline orchestrator: validation, stage sequencing, cancellation, resource cleanup.
// 流水线编排：校验、阶段调度、取消、资源清理。
using System;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class AtoProcessor
    {
        public static void Process(BuildContext ndmf)
        {
            var comps = ndmf.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (comps.Length == 0) return;

            // one component per avatar, must sit on the descriptor object / 每Avatar唯一且挂在descriptor上
            if (comps.Length > 1)
            {
                ErrorReport.ReportError(AtoL10n.Localizer, ErrorSeverity.Error, "error.multiple_components");
                throw new InvalidOperationException("[ATO] multiple AvatarTextureOptimizer components found; aborting build.");
            }
            var settings = comps[0];
#if ATO_VRCSDK3_AVATARS
            if (settings.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() == null)
            {
                ErrorReport.ReportError(AtoL10n.Localizer, ErrorSeverity.Error, "error.no_descriptor");
                throw new InvalidOperationException("[ATO] component must be on the VRCAvatarDescriptor object; aborting build.");
            }
#endif

            AtoLog.Verbose = settings.verboseLog;
            AtoL10n.LanguageOverride = settings.languageOverride;
            AtoLog.Info($"start bake for '{ndmf.AvatarRootObject.name}', tier={settings.qualityTier}");

            var ctx = new AtoContext
            {
                Ndmf = ndmf,
                Settings = settings,
                Quality = settings.EffectiveQuality,
                Platform = DetectPlatform(),
                Pixels = new TexturePixels(),
            };
            ctx.PlatformOverride = ctx.Platform == AtoPlatform.PC ? settings.pcOverride
                : ctx.Platform == AtoPlatform.Android ? settings.androidOverride : settings.iosOverride;

            var total = AtoLog.Time("ATO total");
            try
            {
                AtoExtensions.FireBefore(ctx);

                AtoExtensions.RunCustomStages(ctx, 0, 100);
                ScanStage.Run(ctx);          // 100
                AtoExtensions.RunCustomStages(ctx, 100, 300);
                IslandStage.Run(ctx);        // 300
                AtoExtensions.RunCustomStages(ctx, 300, 400);
                QualityStage.Run(ctx);       // 400
                AtoExtensions.RunCustomStages(ctx, 400, 500);
                PackStage.Run(ctx);          // 500
                AtoExtensions.RunCustomStages(ctx, 500, 600);
                BakeStage.Run(ctx);          // 600
                AtoExtensions.RunCustomStages(ctx, 600, 700);
                RewriteStage.Run(ctx);       // 700
                AtoExtensions.RunCustomStages(ctx, 700, 800);
                FinalizeStage.Run(ctx);      // 800
                AtoExtensions.RunCustomStages(ctx, 800, 900);

                AtoExtensions.FireAfter(ctx);
                AtoReport.Emit(ctx);
            }
            catch (AtoCancelledException)
            {
                // cancel: abort build, keep temp assets on disk, release resources in finally.
                // 取消：终止构建，保留磁盘临时资产，finally 中释放资源。
                ctx.Stats.Cancelled = true;
                ErrorReport.ReportError(AtoL10n.Localizer, ErrorSeverity.Error, "report.cancelled");
                AtoLog.Warn("bake cancelled by user; aborting build and releasing resources.");
                throw;
            }
            finally
            {
                total.Dispose();
                IslandStage.UvCache.Clear();
                Resampler.Cleanup();
                ctx.Dispose();
                AtoProgress.Clear();
                // remove ourselves from the baked avatar / 从成品移除自身组件
                if (settings != null) UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        private static AtoPlatform DetectPlatform()
        {
#if UNITY_ANDROID
            return AtoPlatform.Android;
#elif UNITY_IOS
            return AtoPlatform.iOS;
#else
            return AtoPlatform.PC;
#endif
        }
    }
}
