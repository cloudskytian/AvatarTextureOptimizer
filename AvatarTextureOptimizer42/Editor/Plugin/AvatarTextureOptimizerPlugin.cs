using System;
using System.Diagnostics;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer;
using nadena.dev.ndmf;
using nadena.dev.ndmf.fluent;
using UnityEngine;

[assembly: ExportsPlugin(typeof(Net.Fosa.AvatarTextureOptimizer.Editor.AtoNdmfPlugin))]

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// NDMF plugin registration and pass ordering.
    /// NDMF 插件注册与执行顺序定义。
    /// </summary>
    [RunsOnAllPlatforms]
    internal sealed class AtoNdmfPlugin : Plugin<AtoNdmfPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Configure()
        {
            Sequence seq = InPhase(BuildPhase.Optimizing);
            seq.AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("nadena.dev.modular-avatar.late-transform-stages")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run(AtoValidatePass.Instance)
                .Then.Run(AtoCollectPass.Instance)
                .Then.Run(AtoAnalyzePass.Instance)
                .Then.Run(AtoPlanPass.Instance)
                .Then.Run(AtoExecutePlanPass.Instance)
                .Then.Run(AtoReportPass.Instance)
                .Then.Run(AtoCleanupPass.Instance);
        }

        protected override void OnUnhandledException(Exception e)
        {
            AtoLog.Error($"Unhandled exception: {e}");
            ErrorReport.ReportException(e);
        }
    }

    /// <summary>
    /// Validates mount rules and initializes build state.
    /// 校验挂载规则并初始化构建状态。
    /// </summary>
    internal sealed class AtoValidatePass : Pass<AtoValidatePass>
    {
        protected override void Execute(BuildContext context)
        {
            var timer = Stopwatch.StartNew();
            var state = context.GetState<AtoSessionState>();
            state.Report.StageTimesMs[DisplayName] = 0.0;

            var components = context.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (components.Length == 0)
            {
                state.Enabled = false;
                AtoIssues.ReportInfo(context.AvatarRootObject, "Warnings:NoComponent", context.AvatarRootObject);
                timer.Stop();
                state.Report.StageTimesMs[DisplayName] = timer.Elapsed.TotalMilliseconds;
                return;
            }

            state.Component = components[0];
            if (components.Length != 1 || !AtoReflection.IsAvatarDescriptorRoot(state.Component.gameObject) || state.Component.gameObject != context.AvatarRootObject)
            {
                state.Enabled = false;
                state.Abort = true;
                AtoIssues.ReportError(state.Component, "Errors:InvalidMount", state.Component.gameObject);
                AtoLog.Error("Invalid mount detected. Exactly one AvatarTextureOptimizer must exist on the VRCAvatarDescriptor root object.");
                timer.Stop();
                state.Report.StageTimesMs[DisplayName] = timer.Elapsed.TotalMilliseconds;
                return;
            }

            state.Enabled = state.Component.EnableOptimization;
            if (!state.Enabled)
            {
                AtoLog.Info("Optimization is disabled on the component; only cleanup behavior will run on the baked clone.");
            }

            timer.Stop();
            state.Report.StageTimesMs[DisplayName] = timer.Elapsed.TotalMilliseconds;
            AtoLog.Stage(DisplayName, timer, $"enabled={state.Enabled}, abort={state.Abort}, component={state.Component.name}");
        }
    }

    /// <summary>
    /// Collects current-scene analysis data.
    /// 收集当前场景中的分析数据。
    /// </summary>
    internal sealed class AtoCollectPass : Pass<AtoCollectPass>
    {
        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<AtoSessionState>();
            if (!state.Enabled || state.Abort || state.Component == null)
            {
                return;
            }

            var timer = Stopwatch.StartNew();
            using var progress = new AtoProgressScope("Avatar Texture Optimizer", state.Component.General.EnableProgressBar, state.Component.General.EnableCancellation);
            progress.Report("Collecting renderers, materials, textures, and animation bindings...", 0.1f, ref state.Cancelled);
            if (state.Cancelled)
            {
                state.Abort = true;
                AtoIssues.ReportError(state.Component, "Errors:BuildCancelled", state.Component.gameObject);
                return;
            }

            AtoScanner.Collect(context, state);
            progress.Report("Collection finished.", 1.0f, ref state.Cancelled);
            timer.Stop();
            state.Report.StageTimesMs[DisplayName] = timer.Elapsed.TotalMilliseconds;
            AtoLog.Stage(DisplayName, timer, $"renderers={state.Report.RendererCount}, materials={state.Report.MaterialCount}, textures={state.Report.UniqueTextureCount}, clips={state.Report.AnimationClipCount}");
        }
    }

    /// <summary>
    /// Applies conservative decision rules for this milestone.
    /// 为当前里程碑应用保守的决策规则。
    /// </summary>
    internal sealed class AtoAnalyzePass : Pass<AtoAnalyzePass>
    {
        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<AtoSessionState>();
            if (!state.Enabled || state.Abort || state.Component == null)
            {
                return;
            }

            var timer = Stopwatch.StartNew();
            using var progress = new AtoProgressScope("Avatar Texture Optimizer", state.Component.General.EnableProgressBar, state.Component.General.EnableCancellation);
            progress.Report("Analyzing safe subset, whitelist propagation, and fallback reasons...", 0.4f, ref state.Cancelled);
            if (state.Cancelled)
            {
                state.Abort = true;
                AtoIssues.ReportError(state.Component, "Errors:BuildCancelled", state.Component.gameObject);
                return;
            }

            if (state.Report.UnsupportedCount > 0)
            {
                AtoIssues.ReportWarning(state.Component, "Warnings:UnsupportedTextureUsage", state.Component.gameObject);
            }

            AtoIssues.ReportWarning(state.Component, "Warnings:AnalysisOnly", state.Component.gameObject);
            progress.Report("Analysis finished.", 1.0f, ref state.Cancelled);
            timer.Stop();
            state.Report.StageTimesMs[DisplayName] = timer.Elapsed.TotalMilliseconds;
            AtoLog.Stage(DisplayName, timer, $"candidates={state.ScanResult.TextureUsages.Count(x => x.Decision == AtoTextureDecision.Candidate)}, fallback={state.Report.UnsupportedCount}, whitelist={state.Report.WhitelistHitCount}");
        }
    }

    /// <summary>
    /// Builds the current conservative plan model.
    /// 构建当前保守计划模型。
    /// </summary>
    internal sealed class AtoPlanPass : Pass<AtoPlanPass>
    {
        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<AtoSessionState>();
            if (!state.Enabled || state.Abort || state.Component == null)
            {
                return;
            }

            var timer = Stopwatch.StartNew();
            state.Plan = AtoPlanner.BuildPlan(state);
            timer.Stop();
            state.Report.StageTimesMs[DisplayName] = timer.Elapsed.TotalMilliseconds;
            AtoLog.Stage(DisplayName, timer, $"uvGroups={state.Plan.UvGroupPlans.Count}, typeGroups={state.Plan.TextureTypeGroups.Count}");
        }
    }

    /// <summary>
    /// Safe placeholder execution pass.
    /// 安全占位执行阶段。
    /// </summary>
    internal sealed class AtoExecutePlanPass : Pass<AtoExecutePlanPass>
    {
        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<AtoSessionState>();
            if (state.Component == null || state.Abort)
            {
                return;
            }

            var timer = Stopwatch.StartNew();
            if (!state.Enabled)
            {
                AtoLog.Info("Execution skipped because optimization is disabled.");
            }
            else
            {
                AtoExecutor.Execute(context, state);
                AtoLog.Info($"Execution created textures={state.Report.ExecutedTextureCount}, atlases={state.Report.ExecutedAtlasCount}, meshes={state.Report.ExecutedMeshCount}, materials={state.Report.ExecutedMaterialCount}.");
            }

            timer.Stop();
            state.Report.StageTimesMs[DisplayName] = timer.Elapsed.TotalMilliseconds;
            AtoLog.Stage(DisplayName, timer, "safe execution path completed");
        }
    }

    /// <summary>
    /// Emits build summary to the console.
    /// 向控制台输出构建总结。
    /// </summary>
    internal sealed class AtoReportPass : Pass<AtoReportPass>
    {
        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<AtoSessionState>();
            if (state.Component == null)
            {
                return;
            }

            var timer = Stopwatch.StartNew();
            AtoReporting.EmitSummary(state);
            timer.Stop();
            state.Report.StageTimesMs[DisplayName] = timer.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>
    /// Removes ATO build-time components from the baked avatar clone.
    /// 从烘焙后的 Avatar 成品上移除 ATO 构建期组件。
    /// </summary>
    internal sealed class AtoCleanupPass : Pass<AtoCleanupPass>
    {
        protected override void Execute(BuildContext context)
        {
            var timer = Stopwatch.StartNew();
            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true))
            {
                UnityEngine.Object.DestroyImmediate(component);
            }

            timer.Stop();
            var state = context.GetState<AtoSessionState>();
            state.Report.StageTimesMs[DisplayName] = timer.Elapsed.TotalMilliseconds;
            AtoLog.Stage(DisplayName, timer, "build-time components removed from processed avatar clone");
        }
    }
}
