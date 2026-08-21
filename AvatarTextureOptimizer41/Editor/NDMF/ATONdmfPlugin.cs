using nadena.dev.ndmf;

// NDMF plugin declaration. Runs in the Optimizing phase, after Modular Avatar (Transforming) and
// before Avatar Optimizer (same phase, explicit weak constraint; optional when AAO is absent).
// NDMF 插件声明。在 Optimizing 阶段运行：Modular Avatar（Transforming 阶段）之后、
// Avatar Optimizer（同阶段，显式弱约束；AAO 缺席时自动忽略）之前。

[assembly: ExportsPlugin(typeof(Net.Fosa.AvatarTextureOptimizer.Editor.Ndmf.ATONdmfPlugin))]

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Ndmf
{
    [RunsOnAllPlatforms]
    public sealed class ATONdmfPlugin : Plugin<ATONdmfPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "AvatarTextureOptimizer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .Run("AvatarTextureOptimizer: 贴图优化主流程 / main texture optimization", ATORunner.Run)
                .BeforePlugin("com.anatawa12.avatar-optimizer");
        }
    }
}
