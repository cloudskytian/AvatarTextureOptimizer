// Avatar Texture Optimizer / 头像贴图优化器
// NDMF plugin entry point. Schedules the ATO pass in the Optimizing phase,
// after Modular Avatar (Transforming work is fully settled) and before AAO
// (so AAO still sees pre-optimization state when needed, and our evacuation
// registrations reach it). Dangling constraints are tolerated when the other
// plugins are absent (verified in NDMF 1.14: unresolved constraints are
// simply ignored — AAO itself constrains against MA the same way).
// NDMF 插件入口。把 ATO pass 排在 Optimizing 阶段：在 Modular Avatar 之后
//（其 Transforming 工作已全部落定）、在 AAO 之前（AAO 仍能看到优化前状态，
// 且我们的 UV 通道转移登记能送达它）。对方缺失时悬空约束被容忍
//（NDMF 1.14 已验证：未解析的约束会被忽略；AAO 对 MA 亦然）。

using System;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;

[assembly: ExportsPlugin(typeof(FOSA.AvatarTextureOptimizer.Editor.ATOPlugin))]

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// ATO NDMF plugin. Ordering: after Modular Avatar, before AAO.
    /// ATO 的 NDMF 插件。顺序：Modular Avatar 之后，AAO 之前。
    /// </summary>
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        // Verified against the source of both plugins:
        //   AAO 1.9.17 OptimizerPlugin.QualifiedName => "com.anatawa12.avatar-optimizer"
        //   MA     PluginDefinition.QualifiedName     => "nadena.dev.modular-avatar"
        // 已对照二者源码核实上述限定名。
        private const string AaoQualifiedName = "com.anatawa12.avatar-optimizer";
        private const string MaQualifiedName = "nadena.dev.modular-avatar";

        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";

        public override string DisplayName => "Avatar Texture Optimizer";

        public override Color? ThemeColor => new Color(0.24f, 0.55f, 0.91f);

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                {
                    seq.Run("ATO: Validate & Optimize", Execute)
                        .AfterPlugin(MaQualifiedName)
                        .BeforePlugin(AaoQualifiedName);
                });
        }

        private static void Execute(BuildContext ctx)
        {
            using (var pipeline = new ATOPipeline(ctx))
            {
                if (!pipeline.Validate(out var component)) return; // no component -> inert / 无组件则不动
                pipeline.Run(component);
            }
        }

        protected override void OnUnhandledException(Exception e)
        {
            // Never let an internal ATO fault silently corrupt a build: log loudly
            // and rethrow via the default handler so NDMF aborts the build.
            // 绝不让 ATO 内部故障静默污染构建：响亮记录并经默认处理继续抛出，
            // 由 NDMF 中止本次构建。
            Debug.LogException(new Exception("[ATO] internal error / 内部错误", e));
            base.OnUnhandledException(e);
        }
    }
}
