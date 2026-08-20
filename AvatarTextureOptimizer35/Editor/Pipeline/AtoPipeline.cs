using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// ATO build pass: entry point of the whole pipeline. / ATO 构建 pass：整条流水线的入口。
    ///
    /// Runs: validation → stages → final report. Cancellation aborts the bake but keeps
    /// on-disk temporary assets; CPU/GPU/memory resources are released. /
    /// 执行：校验 → 各阶段 → 最终报告。取消时终止烘焙但保留磁盘临时资产，并释放 CPU/GPU/内存资源。
    /// </summary>
    internal sealed class AtoBuildPass : Pass<AtoBuildPass>
    {
        protected override void Execute(BuildContext context)
        {
            // ---- validation of the AtoAvatarRoot component ----
            // 校验 AtoAvatarRoot 组件
            var roots = new List<AtoAvatarRoot>();
            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<AtoAvatarRoot>(true))
            {
                if (component != null) roots.Add(component);
            }

            if (roots.Count == 0)
            {
                // Tool not used on this avatar: do nothing. / 该 Avatar 未使用本工具：不做任何事。
                return;
            }

            if (roots.Count > 1)
            {
                var e = new AtoConsoleEntry(
                    string.Format(AtoLoc.Lookup("en", "error.multipleRoots"), context.AvatarRootObject.name),
                    ErrorSeverity.Error);
                e.AddReference(ObjectRegistry.GetReference(roots[0]));
                ErrorReport.ReportError(e);
                AtoLog.Error($"Multiple AtoAvatarRoot components ({roots.Count}) on {context.AvatarRootObject.name}: only one is allowed. Bake aborted.");
                return;
            }

            var root = roots[0];
            if (!AtoVrcSdkIntegration.HasVrcAvatarDescriptor(root.gameObject))
            {
                var e = new AtoConsoleEntry(
                    string.Format(AtoLoc.Lookup("en", "error.noVrcDescriptor"), root.gameObject.name),
                    ErrorSeverity.Error);
                e.AddReference(ObjectRegistry.GetReference(root));
                ErrorReport.ReportError(e);
                AtoLog.Error($"AtoAvatarRoot on '{root.gameObject.name}' has no VRCAvatarDescriptor: bake aborted.");
                return;
            }

            // ---- build state ----
            var state = context.GetState<AtoBuildState>();
            state.Settings = root.settings;
            state.Component = root;
            state.LanguageCode = AtoLoc.ResolveCode(root.settings.language);
            AtoLog.Level = root.settings.logLevel;

            AtoLog.Info($"=== ATO build started for {context.AvatarRootObject.name} (language: {state.LanguageCode}) ===");

            try
            {
                var ctx = new AtoContext(context, state);
                var pipeline = new AtoPipeline(ctx);
                pipeline.Run();
            }
            catch (OperationCanceledException)
            {
                AtoLog.Warn(state.Tr("error.cancelled"));
                state.EndProgress();
                AtoRuntimeCache.ReleaseAll();
            }
            catch (Exception ex)
            {
                AtoLog.Error($"ATO internal error: {ex}");
                state.EndProgress();
                AtoRuntimeCache.ReleaseAll();
                throw; // NDMF reports it. / 交给 NDMF 报告。
            }
        }
    }

    /// <summary>
    /// The pipeline: runs all stages in order with progress and cancellation support. /
    /// 流水线：按顺序执行全部阶段，支持进度与取消。
    /// </summary>
    internal sealed class AtoPipeline
    {
        private readonly AtoContext _ctx;

        public AtoPipeline(AtoContext ctx)
        {
            _ctx = ctx;
        }

        public void Run()
        {
            var stages = new IAtoStage[]
            {
                new AtoStageScan(),
                new AtoStageAnimations(),
                new AtoStageDedupeTextures(),
                new AtoStageIslands(),
                new AtoStageQuality(),
                new AtoStagePacking(),
                new AtoStageCompose(),
                new AtoStageMeshes(),
                new AtoStageDedupeAssets(),
                new AtoStageReferences(),
                new AtoStageImport(),
                new AtoStageRemoveSelf(),
            };

            foreach (var stage in stages)
            {
                _ctx.State.ThrowIfCancelled();
                var name = _ctx.State.Tr("stage." + stage.I18nKey);
                AtoLog.Info($"--- stage: {name} ---");
                _ctx.State.BeginProgress(name);
                try
                {
                    using (AtoLog.Time(name))
                    {
                        stage.Run(_ctx);
                    }
                }
                finally
                {
                    _ctx.State.EndProgress();
                }
            }

            AtoReport.Write(_ctx.State);
            AtoLog.Info("=== ATO build finished ===");
        }
    }

    /// <summary>
    /// A single pipeline stage. / 单个流水线阶段。
    /// </summary>
    internal interface IAtoStage
    {
        /// <summary>i18n key suffix ("stage.&lt;key&gt;"). / i18n 键后缀（"stage.&lt;key&gt;"）。</summary>
        string I18nKey { get; }
        void Run(AtoContext ctx);
    }
}
