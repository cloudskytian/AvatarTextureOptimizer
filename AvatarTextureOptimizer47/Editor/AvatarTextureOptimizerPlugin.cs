using System;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;

[assembly: ExportsPlugin(typeof(Fosa.AvatarTextureOptimizer.Editor.AvatarTextureOptimizerPlugin))]

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>EN: NDMF ordering: after Modular Avatar and before AAO. ZH: NDMF 排序：在 Modular Avatar 后、AAO 前。</summary>
    internal sealed class AvatarTextureOptimizerPlugin : Plugin<AvatarTextureOptimizerPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .WithRequiredExtensions(new[] { typeof(AnimatorServicesContext) }, sequence =>
                    sequence.Run(AvatarTextureOptimizerPass.Instance));
        }

        protected override void OnUnhandledException(Exception exception)
        {
            ErrorReport.ReportException(exception);
        }
    }
}
