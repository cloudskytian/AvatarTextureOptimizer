// NDMF plugin declaration: runs in Optimizing phase, after Modular Avatar, before AAO.
// NDMF 插件声明：Optimizing 阶段，MA 之后、AAO 之前。
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;

[assembly: ExportsPlugin(typeof(net.fosa.ato.editor.AtoPlugin))]

namespace net.fosa.ato.editor
{
    public class AtoPlugin : Plugin<AtoPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                {
                    seq.Run("Optimize Textures (ATO)", ctx => AtoProcessor.Process(ctx));
                });
        }
    }
}
