using System;
using nadena.dev.ndmf;
using UnityEngine;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;

[assembly: ExportsPlugin(typeof(Fosa.AvatarTextureOptimizer.Editor.NDMF.ATOPlugin))]

namespace Fosa.AvatarTextureOptimizer.Editor.NDMF
{
    // NDMF 插件注册。
    // 处理时机：Modular Avatar 全部执行之后（含其在 Optimizing 相位的收尾 pass）、AAO 执行之前。
    // NDMF plugin registration.
    // Timing: after all of Modular Avatar (including its late Optimizing-phase pass) and before Avatar Optimizer.
    [RunsOnAllPlatforms]
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";

        public override string DisplayName => "Avatar Texture Optimizer";

        public override Color? ThemeColor => new Color(0.20f, 0.78f, 0.35f, 1f);

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("nadena.dev.modular-avatar.late-transform-stages")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run("Avatar Texture Optimizer", ctx => new ATOBuildProcess().Run(ctx));
        }

        protected override void OnUnhandledException(Exception e)
        {
            ATOLog.Error("未处理异常 / Unhandled exception: " + e);
            base.OnUnhandledException(e);
        }
    }
}
