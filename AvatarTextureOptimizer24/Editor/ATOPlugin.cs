// ============================================================================
// ATOPlugin.cs — NDMF 插件入口 / NDMF plugin entry point
// (EN) Registers ATO as an NDMF plugin running in the Optimizing phase,
//      ordered AFTER Modular Avatar (Transforming) and BEFORE Avatar Optimizer.
// (ZH) 将 ATO 注册为 NDMF 插件，运行于 Optimizing 阶段，
//      顺序在 MA（Transforming）之后、AAO 之前。
// ============================================================================

using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(Fosa.AvatarTextureOptimizer.ATOPlugin))]

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// (EN) NDMF plugin. Runs in Optimizing phase, before AAO.
    /// (ZH) NDMF 插件。运行于 Optimizing 阶段，在 AAO 之前。
    /// </summary>
    public class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "ATO: Avatar Texture Optimizer";

        protected override void Configure()
        {
            // 主序列 / main sequence
            InPhase(BuildPhase.Optimizing)
                .Run(ATOPasses.ValidatePass.Instance)
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Then.Run(ATOPasses.OptimizePass.Instance)
                .BeforePlugin("com.anatawa12.avatar-optimizer");
        }
    }
}
