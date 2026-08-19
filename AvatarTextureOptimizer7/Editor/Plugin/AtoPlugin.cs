using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;

[assembly: ExportsPlugin(typeof(Fosa.AvatarTextureOptimizer.Editor.AtoPlugin))]

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// NDMF plugin. Runs in Optimizing after Modular Avatar (including late-transform)
    /// and before Avatar Optimizer, matching "after MA, before AAO".
    /// NDMF 插件。在 Optimizing 阶段、Modular Avatar（含 late-transform）之后、
    /// Avatar Optimizer 之前运行，对应「MA 之后、AAO 之前」。
    /// </summary>
    public sealed class AtoPlugin : Plugin<AtoPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";
        public override Color? ThemeColor => new Color(0.20f, 0.55f, 0.85f, 1f);

        protected override void Configure()
        {
            InPhase(BuildPhase.Resolving)
                .Run(AtoValidatePass.Instance);

            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("nadena.dev.modular-avatar.late-transform-stages")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                {
                    seq.Run(AtoOptimizePass.Instance);
                });
        }

        protected override void OnUnhandledException(System.Exception e)
        {
            if (e is AtoCancelledException)
            {
                ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.Information, "error.cancelled");
                return;
            }

            ErrorReport.ReportException(e);
        }
    }
}
