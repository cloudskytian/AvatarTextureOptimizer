// English: NDMF plugin registration. Runs in Optimizing after Modular Avatar, before AAO.
// 中文：NDMF 插件注册。在 Optimizing 阶段、Modular Avatar 之后、AAO 之前执行。
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(net.fosa.ato.editor.AtoPlugin))]

namespace net.fosa.ato.editor
{
    [RunsOnAllPlatforms]
    public sealed class AtoPlugin : Plugin<AtoPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";
        public override Color? ThemeColor => new Color(0.15f, 0.72f, 0.62f, 1f);

        protected override void Configure()
        {
            // MA runs in Transforming; we run in Optimizing so we are after MA automatically.
            // AAO QualifiedName is "com.anatawa12.avatar-optimizer" (verified in OptimizerPlugin.cs).
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .Run(AtoOptimizePass.Instance)
                .BeforePlugin("com.anatawa12.avatar-optimizer");
        }
    }
}
