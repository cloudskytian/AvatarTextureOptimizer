// SPDX-License-Identifier: MIT
// EN: NDMF plugin definition and passes. Runs in the Optimizing phase, after Modular Avatar and before
//     anatawa12's Avatar Optimizer.
// ZH: NDMF 插件定义与 Pass。运行于 Optimizing 阶段，即 Modular Avatar 之后、
//     anatawa12 的 Avatar Optimizer 之前。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Net.Fosa.AvatarTextureOptimizer;
using Net.Fosa.AvatarTextureOptimizer.Editor;
using UnityEngine;

[assembly: ExportsPlugin(typeof(ATOPlugin))]

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: Plugin entry point.
    /// ZH: 插件入口。
    /// </summary>
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        /// <summary>EN: Qualified name used by other tools for ordering. ZH: 其他工具用于排序的限定名。</summary>
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";

        public override string DisplayName => "Avatar Texture Optimizer";

        public override Color? ThemeColor => new Color(0.22f, 0.62f, 0.86f, 1f);

        protected override void Configure()
        {
            // EN: Validation runs early so a broken setup aborts before anything else happens.
            // ZH: 校验尽早执行，配置有误时在其他处理之前中止。
            InPhase(BuildPhase.Resolving)
                .Run(ATOValidationPass.Instance);

            // EN: The optimisation itself needs the animator services (material swaps, cutoff curves).
            // ZH: 优化本体需要 animator services（材质切换、cutoff 曲线）。
            InPhase(BuildPhase.Optimizing)
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                {
                    seq.Run(ATOMainPass.Instance)
                        .BeforePlugin("com.anatawa12.avatar-optimizer");
                });
        }

        protected override void OnUnhandledException(Exception e)
        {
            Debug.LogException(e);
            ErrorReport.ReportError(new ATOError(ErrorSeverity.Error, "ato:error:internal"));
        }
    }

    /// <summary>
    /// EN: Verifies that exactly one component exists and that it sits on the avatar root.
    /// ZH: 校验组件唯一，且挂在 Avatar 根节点上。
    /// </summary>
    public sealed class ATOValidationPass : Pass<ATOValidationPass>
    {
        public override string DisplayName => "ATO: validate";

        protected override void Execute(BuildContext context)
        {
            var components = context.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (components.Length == 0) return;

            if (components.Length > 1)
            {
                foreach (var c in components)
                    ErrorReport.ReportError(new ATOError(ErrorSeverity.Error, "ato:error:multipleComponents").With(c));
                throw new Exception("[ATO] more than one Avatar Texture Optimizer component on this avatar");
            }

            var component = components[0];
            if (!HasAvatarDescriptor(component.gameObject))
            {
                ErrorReport.ReportError(new ATOError(ErrorSeverity.Error, "ato:error:noDescriptor").With(component));
                throw new Exception("[ATO] the component must be placed on the avatar root (VRCAvatarDescriptor)");
            }
        }

        internal static bool HasAvatarDescriptor(GameObject go)
        {
#if ATO_VRCSDK3_AVATARS
            return go.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() != null;
#else
            // EN: Without the VRChat SDK we only require the component to be on the NDMF avatar root.
            // ZH: 没有 VRChat SDK 时，只要求组件位于 NDMF 的 Avatar 根节点上。
            return true;
#endif
        }
    }

    /// <summary>
    /// EN: The optimisation pass.
    /// ZH: 优化主 Pass。
    /// </summary>
    public sealed class ATOMainPass : Pass<ATOMainPass>
    {
        public override string DisplayName => "ATO: optimize textures";

        protected override void Execute(BuildContext context)
        {
            var component = context.AvatarRootObject.GetComponentInChildren<AvatarTextureOptimizer>(true);
            if (component == null) return;

            var settings = component.settings ?? new ATOSettings();
            if (!string.IsNullOrEmpty(settings.languageOverride))
                nadena.dev.ndmf.localization.LanguagePrefs.Language = settings.languageOverride;

            var log = new ATOLog { Verbose = settings.verboseLogging };
            log.Info("build", $"Avatar Texture Optimizer starting on '{context.AvatarRootObject.name}'");

            using var progress = new ATOProgress(log);
            ATOPipeline pipeline = null;

            try
            {
                pipeline = new ATOPipeline(context, settings, log, progress);

                var asc = context.Extension<AnimatorServicesContext>();
                var roots = asc.ControllerContext.GetAllControllers().Cast<VirtualNode>();
                var clips = ATOAnimationAnalyzer.EnumerateClips(roots);

                pipeline.Run(clips);

                ATOReportBuilder.Publish(log, pipeline.Statistics, settings);
            }
            catch (ATOCancelledException)
            {
                log.Warning("build", ATOL10n.Tr("ato:progress:cancel"));
                ATOReportBuilder.PublishCancelled(log);
                throw;
            }
            catch (Exception e)
            {
                log.Error("build", $"failed: {e}");
                ErrorReport.ReportError(new ATOError(ErrorSeverity.Error, "ato:error:internal"));
                throw;
            }
            finally
            {
                pipeline?.Dispose();

                // EN: NDMF must not ship our component with the built avatar.
                // ZH: NDMF 不应把我们的组件带到成品 Avatar 上。
                foreach (var c in context.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true))
                    UnityEngine.Object.DestroyImmediate(c);

                Resources.UnloadUnusedAssets();
            }
        }
    }

    /// <summary>
    /// EN: Publishes the human readable build report into the NDMF console.
    /// ZH: 把可读的构建报告发布到 NDMF 控制台。
    /// </summary>
    public static class ATOReportBuilder
    {
        public static void Publish(ATOLog log, ATOStatistics stats, ATOSettings settings)
        {
            var summary = ATOL10n.Tr("ato:report:summary",
                stats.AtlasCount,
                stats.TexturesOptimised,
                stats.IslandsPacked,
                stats.OriginalBytes / (1024.0 * 1024.0),
                stats.ResultBytes / (1024.0 * 1024.0),
                stats.SavedPercent,
                log.TotalMs / 1000.0);

            Debug.Log($"{ATOLog.Prefix} {summary}");

            if (settings.timingProfile)
            {
                var lines = new List<string>();
                foreach (var kv in log.Timings) lines.Add($"  {kv.Key}: {kv.Value:F1} ms");
                Debug.Log($"{ATOLog.Prefix} timings\n{string.Join("\n", lines)}");
            }

            ErrorReport.ReportError(new ATOReportInfo(summary, log.BuildDetailText()));
        }

        public static void PublishCancelled(ATOLog log)
        {
            ErrorReport.ReportError(new ATOReportInfo(ATOL10n.Tr("ato:progress:cancel"), log.BuildDetailText()));
        }
    }

    /// <summary>
    /// EN: Informational report entry: the summary is always shown, the details are collapsed.
    /// ZH: 信息型报告条目：摘要始终展示，细节默认折叠。
    /// </summary>
    public sealed class ATOReportInfo : SimpleError
    {
        private readonly string _summary;
        private readonly string _details;

        public ATOReportInfo(string summary, string details)
        {
            _summary = summary;
            _details = details;
        }

        public override nadena.dev.ndmf.localization.Localizer Localizer => ATOL10n.Localizer;
        public override string TitleKey => "ato:report:title";
        public override ErrorSeverity Severity => ErrorSeverity.Information;

        public override string FormatTitle() => ATOL10n.Tr("ato:report:title") + " - " + _summary;
        public override string FormatDetails() => _details;
        public override string FormatHint() => "";
        public override string ToMessage() => ATOLog.Prefix + " " + _summary;
    }
}
