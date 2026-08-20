using System;
using nadena.dev.ndmf;
using nadena.dev.ndmf.fluent;

[assembly: ExportsPlugin(typeof(Fosa.AvatarTextureOptimizer.Editor.ATOPlugin))]

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// NDMF entry point for Avatar Texture Optimizer. / Avatar Texture Optimizer 的 NDMF 入口。
    /// </summary>
    [RunsOnAllPlatforms]
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Configure()
        {
            // Run after Modular Avatar and before AAO. / 在 Modular Avatar 之后、AAO 之前运行。
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run(ATOPass.Instance);
        }

        protected override void OnUnhandledException(Exception exception)
        {
            if (exception is ATOUserCancelledException)
            {
                UnityEngine.Debug.Log("[ATO] Cancelled; NDMF build is aborted without treating cancellation as a product error. / 已取消，NDMF 构建中止且不作为产品错误。");
                return;
            }
            ErrorReport.ReportException(exception);
        }
    }

    /// <summary>
    /// The single NDMF pass keeps build state local and disposable. / 单一 NDMF Pass，确保构建状态局部且可释放。
    /// </summary>
    public sealed class ATOPass : Pass<ATOPass>
    {
        protected override void Execute(BuildContext context)
        {
            ATOBuildSession.Execute(context);
        }
    }
}
