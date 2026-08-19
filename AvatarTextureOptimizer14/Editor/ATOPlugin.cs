// ATOPlugin — NDMF plugin entry / NDMF 插件入口
// Verified against ndmf-1.14.4 sources: Optimizing phase + BeforePlugin runs us strictly after
// Modular Avatar (Transforming) and before Av3 Optimizer (Optimizing). BeforePlugin is safe when
// AAO is not installed (SolverContext.GetPluginPhases creates empty innate phases).<br>
// 依据 ndmf-1.14.4 源码：Optimizing 阶段 + BeforePlugin(AAO) 保证在 MA(Transforming) 之后、AAO 之前执行；
// AAO 未安装时 BeforePlugin 依然安全（GetPluginPhases 惰性创建空标记）。
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(Fosa.ATO.Editor.ATOPlugin))]

namespace Fosa.ATO.Editor
{
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Configure()
        {
            var seq = InPhase(BuildPhase.Optimizing);
            // Belt-and-braces ordering; Optimizing already implies after MA's Transforming work. / 双保险排序
            seq.AfterPlugin("nadena.dev.modular-avatar");
            seq.Run("Avatar Texture Optimizer", ATOPipeline.Execute)
               .BeforePlugin("com.anatawa12.avatar-optimizer"); // AAO absent => no-op (verified) / AAO 未安装时无副作用
        }
    }

    /// <summary>Pipeline entry point and stage orchestration. / 流水线入口与阶段编排。</summary>
    internal static class ATOPipeline
    {
        internal static void Execute(BuildContext ctx)
        {
            ATOLog.Reset();
            var comps = ctx.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (comps.Length == 0) return; // nothing to do / 未挂载组件

            // ---- Placement validation (fail the build on violation) / 挂载合规校验（违规中止） ----
            if (comps.Length > 1)
            {
                ErrorReport.ReportError(ATOL10n.L, ErrorSeverity.Error, "ato.err.multiple_components", comps.Length);
                throw new InvalidOperationException("ATO: multiple AvatarTextureOptimizer components on avatar");
            }
            var comp = comps[0];
            var descriptor = comp.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                ErrorReport.ReportError(ATOL10n.L, ErrorSeverity.Error, "ato.err.no_descriptor");
                throw new InvalidOperationException("ATO: component must sit on the object owning VRCAvatarDescriptor");
            }

            ATOL10n.OverrideLanguage = comp.languageOverride;
            ATOLog.Verbose = comp.verboseLogging;
            var settings = ATOSettingsSnap.From(comp);
            var pipe = new ATOPipeContext { settings = settings };
            var progress = new StageProgress();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ATOLog.Info(ATOL10n.T("ato.log.begin", ctx.AvatarRootObject.name));

            try
            {
                using (ATOLog.Stage(ATOL10n.T("ato.stage.discovery"))) Stage1_Discovery.Run(ctx, pipe, progress);
                // islands/groups only exist after Stage2 — gate on Stage1 output here / 岛与组在 Stage2 才产生，此处只能看 Stage1 产物
                if (pipe.slotRefs.Count == 0) { ATOLog.Info(ATOL10n.T("ato.log.nothing")); return; }
                using (ATOLog.Stage(ATOL10n.T("ato.stage.uv"))) Stage2_UV.Run(ctx, pipe, progress);
                using (ATOLog.Stage(ATOL10n.T("ato.stage.quality"))) Stage3_Quality.Run(ctx, pipe, progress);
                if (settings.generateAtlas)
                {
                    using (ATOLog.Stage(ATOL10n.T("ato.stage.packing"))) Stage4_Packing.Run(ctx, pipe, progress);
                    using (ATOLog.Stage(ATOL10n.T("ato.stage.bake"))) Stage5_Bake.Run(ctx, pipe, progress);
                }
                // whole-texture path always runs: it also picks up whitelist/abandoned groups left over from atlasing
                // 整图阶段始终执行：图集化之外的白名单组/放弃组也由它处理
                using (ATOLog.Stage(ATOL10n.T("ato.stage.wholescale"))) Stage5b_WholeTexture.Run(ctx, pipe, progress);
                using (ATOLog.Stage(ATOL10n.T("ato.stage.remap"))) Stage6_Remap.Run(ctx, pipe, progress);
                using (ATOLog.Stage(ATOL10n.T("ato.stage.materials"))) Stage7_Apply.Run(ctx, pipe, progress);
                using (ATOLog.Stage(ATOL10n.T("ato.stage.dedup"))) Stage7b_Dedup.Run(ctx, pipe, progress);
                using (ATOLog.Stage(ATOL10n.T("ato.stage.clips"))) Stage7c_Clips.Run(ctx, pipe, progress);
                using (ATOLog.Stage(ATOL10n.T("ato.stage.report"))) Stage8_Report.Run(ctx, pipe, comp, sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                // Cancel keeps on-disk temp assets; native/GPU buffers are released by stage finally blocks.
                // 取消：保留磁盘临时资产；Native/GPU 资源由各阶段 finally 释放。
                ATOLog.Warn(ATOL10n.T("ato.log.cancelled"));
                throw;
            }
            finally
            {
                progress.Clear();
                ImageCache.ReleaseAll(); // release decoded pixel buffers (incl. cancel path) / 释放解码像素缓存（含取消路径）
            }
        }
    }
}
