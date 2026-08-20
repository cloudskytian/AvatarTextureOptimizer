// ============================================================================
// ATO - NDMF plugin registration
// ATO - NDMF 插件注册
//
// Execution order (NDMF 1.14 Plugin/Pass architecture):
//   - The whole ATO pipeline runs as a single pass in BuildPhase.Optimizing,
//     which is after Modular Avatar's Transforming work.
//   - Explicit weak-order constraints place ATO after the
//     "nadena.dev.modular-avatar" plugin and before the
//     "com.anatawa12.avatar-optimizer" (AAO) plugin. Both constraints degrade
//     to harmless phantoms when the other plugin is not installed.
//   - The plugin qualified name is an ASCII name so NDMF's ordinal fallback
//     sort also places it before AAO's deliberately last-sorting name.
// 执行顺序（NDMF 1.14 Plugin/Pass 架构）：
//   - ATO 全部管线作为单个 Pass 运行于 BuildPhase.Optimizing，晚于 Modular
//     Avatar 的 Transforming 工作。
//   - 显式弱序约束将 ATO 置于 "nadena.dev.modular-avatar" 之后、
//     "com.anatawa12.avatar-optimizer"（AAO）之前；对应插件未安装时约束退化
//     为无害幻影。
//   - 插件限定名为 ASCII，NDMF 的 ordinal 回退排序也会把它排在 AAO 刻意最后
//     的命名之前。
// ============================================================================

#region

using System;
using nadena.dev.ndmf;
using nadena.dev.ndmf.fluent;
using net.fosa.AvatarTextureOptimizer.Editor.Core;

#endregion

[assembly: ExportsPlugin(typeof(net.fosa.AvatarTextureOptimizer.Editor.ATOPlugin))]

namespace net.fosa.AvatarTextureOptimizer.Editor
{
    public class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "ATO: Avatar Texture Optimizer";

        protected override void Configure()
        {
            // Discover third-party extensions once. 发现第三方扩展（一次）。
            Api.ATOApiRegistry.AutoDiscover();

            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run(ATOPipelinePass.Instance);
        }

        protected override void OnUnhandledException(Exception e)
        {
            // Route to the NDMF error report so VRChat builds fail with a
            // visible, attributed message.
            // 汇入 NDMF 错误报告，使 VRChat 构建以可见且归属明确的错误失败。
            ErrorReport.ReportException(e);
        }
    }
}
