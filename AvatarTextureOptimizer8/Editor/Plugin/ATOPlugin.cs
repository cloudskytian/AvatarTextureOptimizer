// ATOPlugin.cs
// NDMF plugin registration, pass pipeline, progress bar and cancellation.
// NDMF 插件注册、Pass 管线、进度条与取消支持。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

// Registers ATO with NDMF. / 向 NDMF 注册 ATO。
// Runs after Modular Avatar and before AvatarOptimizer in the Optimizing phase.
// 在 Optimizing 阶段、Modular Avatar 之后、AvatarOptimizer 之前运行。
[assembly: ExportsPlugin(typeof(net.fosa.ato.AvatarTextureOptimizerPlugin))]

namespace net.fosa.ato
{
    /// <summary>NDMF plugin entry. / NDMF 插件入口。</summary>

    /// <summary>NDMF plugin entry. / NDMF 插件入口。</summary>
    public sealed class AvatarTextureOptimizerPlugin : Plugin<AvatarTextureOptimizerPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";

        public override Color? ThemeColor => new Color(0.30f, 0.62f, 0.95f);

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                {
                    seq.Run("ATO: Analyze", RunAnalyzeStage);
                    seq.Run("ATO: Optimize Textures", RunOptimizeStage);
                    seq.Run("ATO: Finalize", RunFinalizeStage);
                });
        }

        // ------------------------------------------------------------------ //
        // Stage entry points / 阶段入口
        // ------------------------------------------------------------------ //
        private void RunAnalyzeStage(BuildContext context)
        {
            using (ATOLog.Stage("plugin.analyze")) ATOProcessor.RunAnalyze(context);
        }

        private void RunOptimizeStage(BuildContext context)
        {
            using (ATOLog.Stage("plugin.optimize")) ATOProcessor.RunOptimize(context);
        }

        private void RunFinalizeStage(BuildContext context)
        {
            using (ATOLog.Stage("plugin.finalize")) ATOProcessor.RunFinalize(context);
        }
    }

    /// <summary>
    /// Thrown when the user cancels the build via the progress bar. Resources are already
    /// released by the processor. / 用户通过进度条取消构建时抛出;资源已由处理器释放。
    /// </summary>
    internal sealed class ATOCancelledException : Exception
    {
        internal ATOCancelledException() : base("Avatar Texture Optimizer: cancelled by user.") { }
    }

    /// <summary>Wraps NDMF-usable cancellation outcome. / NDMF 可用的取消结果包装。</summary>
    internal sealed class ATOCancellationError : SimpleError
    {
        public override Localizer Localizer => ATOLocalization.Localizer;
        public override string TitleKey => "ato.report.cancelled";
        public override ErrorSeverity Severity => ErrorSeverity.Information;
    }

    /// <summary>Lightweight progress helper around EditorUtility progress bars. / 进度条辅助。</summary>
    internal sealed class ATOProgress : IDisposable
    {
        private string _title;
        private bool _disposed;

        internal ATOProgress(string title)
        {
            _title = title;
            EditorUtility.DisplayProgressBar(title, "Initializing...", 0f);
        }

        /// <summary>Report progress; throws ATOCancelledException on cancel. / 汇报进度;取消时抛出异常。</summary>
        internal void Report(string info, float progress)
        {
            if (_disposed) return;
            if (EditorUtility.DisplayCancelableProgressBar(_title, info, Mathf.Clamp01(progress)))
                throw new ATOCancelledException();
        }

        /// <summary>Non-cancelable final step. / 不可取消的收尾。</summary>
        internal void ReportIndeterminate(string info)
        {
            if (_disposed) return;
            EditorUtility.DisplayProgressBar(_title, info, Mathf.Clamp01(0.999f));
        }

        public void Dispose()
        {
            _disposed = true;
            EditorUtility.ClearProgressBar();
        }
    }
}
