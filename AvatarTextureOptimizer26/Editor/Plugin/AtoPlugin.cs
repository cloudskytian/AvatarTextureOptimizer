using System;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.fluent;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

[assembly: ExportsPlugin(typeof(Net.Fosa.AvatarTextureOptimizer.Editor.AtoPlugin))]

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// NDMF plugin entry. Runs in Transforming after Modular Avatar (including late stages) and TexTransTool,
    /// before Avatar Optimizer (which is Optimizing phase).
    /// NDMF 插件入口。在 Transforming 阶段、MA（含 late）与 TTT 之后、AAO 之前运行。
    /// </summary>
    [RunsOnPlatforms(WellKnownPlatforms.VRChatAvatar30)]
    public sealed class AtoPlugin : Plugin<AtoPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";
        public override Color? ThemeColor => new Color(0.20f, 0.72f, 0.55f, 1f);

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("nadena.dev.modular-avatar.late-transform-stages")
                .AfterPlugin("net.rs64.tex-trans-tool")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                {
                    seq.Run(AtoPass.Instance);
                });
        }

        protected override void OnUnhandledException(Exception e)
        {
            AtoLog.Error("Unhandled exception / 未处理异常: " + e);
            ErrorReport.ReportException(e);
        }
    }

    public sealed class AtoPass : Pass<AtoPass>
    {
        public override string DisplayName => "ATO: Optimize Textures";

        protected override void Execute(BuildContext context)
        {
            new AtoPipeline().Run(context);
        }
    }
}
