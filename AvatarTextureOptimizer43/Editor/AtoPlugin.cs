using System;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;
using Fosa.ATO;

[assembly: ExportsPlugin(typeof(Fosa.ATO.Editor.AtoPlugin))]

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// NDMF plugin. Runs in Optimizing, after Modular Avatar, before AAO.
    /// Verified against NDMF 1.14.4 Plugin/Sequence/BuildPhase APIs and
    /// MA QualifiedName "nadena.dev.modular-avatar",
    /// AAO QualifiedName "com.anatawa12.avatar-optimizer".
    /// NDMF 插件。Optimizing 阶段，MA 之后、AAO 之前。
    /// </summary>
    [RunsOnPlatforms(WellKnownPlatforms.VRChatAvatar30)]
    public sealed class AtoPlugin : Plugin<AtoPlugin>
    {
        public override string QualifiedName => AvatarTextureOptimizer.PackageName;
        public override string DisplayName => AvatarTextureOptimizer.DisplayName;
        public override Color? ThemeColor => new Color(0.15f, 0.72f, 0.62f, 1f);

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                {
                    seq.Run(AtoPass.Instance);
                });
        }

        protected override void OnUnhandledException(Exception e)
        {
            if (e is OperationCanceledException)
            {
                ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.Error, "ato.error.cancelled");
                return;
            }
            ErrorReport.ReportException(e);
        }
    }

    public sealed class AtoPass : Pass<AtoPass>
    {
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Execute(BuildContext ctx)
        {
            AtoPipeline.Run(ctx);
        }
    }
}
