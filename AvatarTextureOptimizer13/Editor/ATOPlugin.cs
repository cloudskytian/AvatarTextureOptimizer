// ATO — Avatar Texture Optimizer
// NDMF plugin entry point. Registers the pipeline passes to run after Modular Avatar
// and before Avatar Optimizer (AAO) in the Optimizing build phase.
// NDMF 插件入口。将管线 Pass 注册到 Optimizing 阶段，位于 Modular Avatar 之后、AAO 之前。
//
// Ordering rationale (verified against NDMF 1.14.4 / MA 1.18.2 / AAO 1.9.17 sources):
//   - MA main work runs in Resolving + Transforming; MA's only Optimizing pass is
//     GCGameObjectsPluginPass → we must run AfterPlugin("nadena.dev.modular-avatar").
//   - AAO main work runs in Optimizing → we must run BeforePlugin("com.anatawa12.avatar-optimizer").
//   - NDMF's Sequence.AfterPlugin / Sequence.BeforePlugin constrain the WHOLE sequence
//     (sequenceStart / sequenceEnd phantom passes) against the other plugin's phantom
//     start/end, which is exactly what we want. GetPluginPhases lazily creates phantom
//     phases for any plugin in any phase, so referencing a plugin that has no passes in
//     this phase is safe.
// 顺序依据（已核对 NDMF 1.14.4 / MA 1.18.2 / AAO 1.9.17 源码）：
//   - MA 主逻辑在 Resolving + Transforming；MA 在 Optimizing 仅有一个 GCGameObjectsPluginPass，
//     因此必须 AfterPlugin("nadena.dev.modular-avatar")。
//   - AAO 主逻辑在 Optimizing，因此必须 BeforePlugin("com.anatawa12.avatar-optimizer")。
//   - NDMF 的 Sequence.AfterPlugin / BeforePlugin 用 sequenceStart/sequenceEnd 幻影 Pass
//     与对方插件的幻影 start/end 建立约束，正是所需语义。GetPluginPhases 会为任意插件在
//     任意阶段惰性创建幻影阶段，因此引用在本阶段无 Pass 的插件也安全。

using System;
using nadena.dev.ndmf;
using net.fosa.ato.editor;

[assembly: ExportsPlugin(typeof(net.fosa.ato.editor.ATOPlugin))]

namespace net.fosa.ato.editor
{
    /// <summary>
    /// The ATO NDMF plugin. ATO 的 NDMF 插件。
    /// </summary>
    internal class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "AvatarTextureOptimizer (ATO)";

        protected override void Configure()
        {
            var sequence = InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("nadena.dev.modular-avatar.late-transform-stages")
                // Constrain the WHOLE sequence before AAO. 约束整个序列在 AAO 之前。
                .BeforePlugin("com.anatawa12.avatar-optimizer");

            sequence.Run(Pass0Validate.Instance)
                .Then.Run(Pass1Analyze.Instance)
                .Then.Run(Pass2Optimize.Instance)
                .Then.Run(Pass3Atlas.Instance)
                .Then.Run(Pass4Reassign.Instance)
                .Then.Run(Pass5Dedup.Instance)
                .Then.Run(Pass6ReportCleanup.Instance);
        }

        protected override void OnUnhandledException(Exception e)
        {
            // Let NDMF display the exception in its console. 让 NDMF 在其控制台显示异常。
            ErrorReport.ReportException(e);
        }
    }
}
