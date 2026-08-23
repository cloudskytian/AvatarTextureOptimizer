using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;

[assembly: ExportsPlugin(typeof(Fosa.AvatarTextureOptimizer.Editor.ATOPlugin))]

namespace Fosa.AvatarTextureOptimizer.Editor
{
    [RunsOnAllPlatforms]
    internal sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string DisplayName => "Avatar Texture Optimizer";
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .WithRequiredExtensions(new[] { typeof(AnimatorServicesContext) }, sequence =>
                    sequence.Run("Optimize avatar textures and UV islands", ATOPipeline.Run));
        }
    }
}
