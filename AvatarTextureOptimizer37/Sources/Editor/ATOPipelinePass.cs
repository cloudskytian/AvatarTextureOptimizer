// ============================================================================
// ATO - main pipeline pass
// ATO - 主管线 Pass
//
// The entire ATO pipeline runs as one NDMF pass in BuildPhase.Optimizing:
//
//   0 Validate   1 Analyze   2 Quality   3 Pack     4 Atlas
//   5 Import     6 Dedup     7 Apply     8 Report
//
// All Unity-object mutations are deferred to the Apply stage so a cancel at
// any earlier point leaves the avatar untouched (atomic apply). Cancellation
// throws ATOPipelineCancelledException; fatal config errors throw
// ATOPipelineFatalException - both surface in the NDMF error report.
// ATO 整条管线作为 BuildPhase.Optimizing 中的单个 NDMF Pass 运行：
//   0 校验  1 分析  2 质量  3 装箱  4 图集  5 导入  6 去重  7 应用  8 报告
// 所有对 Unity 对象的改动都推迟到 Apply 阶段，因此在此之前任何时点取消都会使
// Avatar 保持原样（原子应用）。取消抛出 ATOPipelineCancelledException；致命配
// 置错误抛出 ATOPipelineFatalException - 两者都会出现在 NDMF 错误报告中。
// ============================================================================

#region

using System;
using nadena.dev.ndmf;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using net.fosa.AvatarTextureOptimizer.Editor.I18n;
using net.fosa.AvatarTextureOptimizer.Editor.Stages;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor
{
    public class ATOPipelinePass : Pass<ATOPipelinePass>
    {
        protected override void Execute(BuildContext context)
        {
            // Validate component placement first (throws on hard errors).
            // 先校验组件挂载（硬性错误直接抛出）。
            var ctx = new ATOContext();
            using var log = new ATOLog(ATOLogMask.None, false);
            ctx.Log = log;

            var component = ctx.Validate(context.AvatarRootObject);
            if (component == null)
            {
                return; // disabled or absent - do nothing 禁用或不存在 - 不处理
            }

            using var session = new ATOBuildSession();
            ctx.Session = session;
            ctx.Component = component;
            ctx.Log = new ATOLog(component.LogMask, component.VerboseLogging);

            var progressWindow = EditorWindow.GetWindow<ATOProgressWindow>(false);
            progressWindow.Attach(session);

            ATOI18n.Apply(component);

            // capture settings BEFORE Apply (the component is destroyed there)
            // 在 Apply（销毁组件）之前捕获设置
            bool verbose = component.VerboseLogging;

            var stageNames = new[]
            {
                "Validate 校验", "Analyze 分析", "Quality 质量缩放", "Pack 装箱",
                "Atlas 图集合成", "Import 导入参数", "Dedup 去重", "Apply 应用", "Report 报告",
            };

            try
            {
                // Stage 0: validation already done above. 阶段0：校验已在上面完成。
                session.SetStage(0, stageNames.Length, stageNames[0]);
                session.Check("Validate 校验");
                ctx.Log.Info(ATOLogMask.Analysis,
                    $"ATO pipeline started on \"{context.AvatarRootObject.name}\" " +
                    "(tier={component.QualityTier}, atlas={component.GenerateAtlas}, " +
                    "density={component.MinDensity}-{component.MaxDensity} px/m). " +
                    "ATO 管线启动。");

                // Stages 1..7  阶段 1..7
                session.SetStage(1, stageNames.Length, stageNames[1]);
                AnalysisStage.Execute(ctx, context);

                session.SetStage(2, stageNames.Length, stageNames[2]);
                QualityStage.Execute(ctx, context);

                session.SetStage(3, stageNames.Length, stageNames[3]);
                PackStage.Execute(ctx, context);

                session.SetStage(4, stageNames.Length, stageNames[4]);
                AtlasStage.Execute(ctx, context);

                session.SetStage(5, stageNames.Length, stageNames[5]);
                ImportStage.Execute(ctx, context);

                session.SetStage(6, stageNames.Length, stageNames[6]);
                DedupStage.Execute(ctx, context);

                session.SetStage(7, stageNames.Length, stageNames[7]);
                ApplyStage.Execute(ctx, context);

                // Stage 8: report (component already removed in Apply)
                // 阶段8：报告（组件已在 Apply 中移除，使用捕获的 verbose 标志）
                session.SetStage(8, stageNames.Length, stageNames[8]);
                ReportStage.Execute(ctx, context, verbose);
            }
            catch (ATOPipelineCancelledException)
            {
                // Resource release happens in stage finally-blocks; the avatar
                // is untouched because Apply had not completed.
                // 资源释放发生在各阶段的 finally 中；Apply 未完成故 Avatar 未被
                // 修改。
                throw;
            }
            finally
            {
                progressWindow.Detach();
            }
        }
    }
}
