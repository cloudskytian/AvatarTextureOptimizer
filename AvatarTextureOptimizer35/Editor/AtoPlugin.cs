using System;
using nadena.dev.ndmf;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// NDMF plugin definition for ATO. / ATO 的 NDMF 插件定义。
    ///
    /// Runs in the Optimizing phase, AFTER Modular Avatar (its last optimizing pass is
    /// GCGameObjectsPluginPass) and BEFORE Avatar Optimizer (which uses the \uFFDC-namespace
    /// trick to sort last, but we add an explicit BeforePlugin constraint). /
    /// 运行于 Optimizing 阶段：Modular Avatar 之后（其 Optimizing 阶段最后一个 pass 是
    /// GCGameObjectsPluginPass）、Avatar Optimizer 之前（AAO 用 \uFFDC 命名空间技巧排最后，
    /// 但我们额外加了显式 BeforePlugin 约束）。
    ///
    /// All constraints are WeakOrder constraints (verified in NDMF source): if a plugin is
    /// not installed, the constraint is still satisfiable. / 全部约束为 WeakOrder（已读 NDMF
    /// 源码确认）：目标插件未安装时约束依然可满足。
    /// </summary>
    internal sealed class AtoPlugin : Plugin<AtoPlugin>
    {
        public override string DisplayName => "ATO: Avatar Texture Optimizer";
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("net.rs64.tex-trans-tool")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run(AtoBuildPass.Instance);
        }

        protected override void OnUnhandledException(Exception e)
        {
            ErrorReport.ReportException(e);
        }
    }
}
