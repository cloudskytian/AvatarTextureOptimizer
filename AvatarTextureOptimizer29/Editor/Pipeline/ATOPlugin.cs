// NDMF plugin declaration: runs in Optimizing, after Modular Avatar, before AAO
// (ordering verified against plugin QualifiedNames in both packages' sources).
// NDMF 插件声明：Optimizing 阶段，MA 之后 AAO 之前（顺序经两包源码 QualifiedName 核实）。

using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    [ExportsPlugin(typeof(AvatarTextureOptimizerPlugin))]
    public class AvatarTextureOptimizerPlugin : Plugin<AvatarTextureOptimizerPlugin>
    {
        public override string QualifiedName => AvatarTextureOptimizer.PluginQualifiedName;
        public override string DisplayName => "ATO: Avatar Texture Optimizer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                {
                    seq.Run(Pass<ATOPass>.Instance);
                });
        }
    }
}
