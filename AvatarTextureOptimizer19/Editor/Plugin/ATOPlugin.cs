// English: NDMF plugin registration. Runs in Optimizing after MA / TTT and before AAO.
// 中文：NDMF 插件注册。在 Optimizing 阶段、MA/TTT 之后、AAO 之前执行。
using System;
using nadena.dev.ndmf;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

[assembly: ExportsPlugin(typeof(Net.Fosa.AvatarTextureOptimizer.Editor.ATOPlugin))]

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName
        {
            get { return "net.fosa.avatar-texture-optimizer"; }
        }

        public override string DisplayName
        {
            get { return "Avatar Texture Optimizer"; }
        }

        public override Color? ThemeColor
        {
            get { return new Color(0.23f, 0.72f, 0.55f, 1f); }
        }

        protected override void Configure()
        {
            // English: After Modular Avatar (including late-transform) and TexTransTool; before Avatar Optimizer.
            // 中文：排在 Modular Avatar（含 late-transform）与 TexTransTool 之后、AAO 之前。
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("nadena.dev.modular-avatar.late-transform-stages")
                .AfterPlugin("net.rs64.tex-trans-tool")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .WithRequiredExtension(typeof(nadena.dev.ndmf.animator.AnimatorServicesContext), seq =>
                {
                    seq.Run(ATOOptimizePass.Instance);
                });
        }

        protected override void OnUnhandledException(Exception e)
        {
            Debug.LogException(e);
            ErrorReport.ReportException(e);
        }
    }

    internal sealed class ATOOptimizePass : Pass<ATOOptimizePass>
    {
        public override string DisplayName
        {
            get { return "Avatar Texture Optimizer"; }
        }

        protected override void Execute(BuildContext context)
        {
            ATOPipeline.Run(context);
        }
    }
}
