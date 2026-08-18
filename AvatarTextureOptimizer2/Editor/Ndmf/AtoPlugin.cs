using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(Net.Fosa.AvatarTextureOptimizer.Editor.AtoPlugin))]

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// NDMF plugin. Runs in Optimizing after Modular Avatar, before AAO.
    /// NDMF 插件：Optimizing 阶段，MA 之后、AAO 之前。
    /// </summary>
    [RunsOnAllPlatforms]
    public sealed class AtoPlugin : Plugin<AtoPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";
        public override Color? ThemeColor => new Color(0.35f, 0.72f, 0.85f);

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("nadena.dev.modular-avatar.late-transform-stages")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run(AtoOptimizePass.Instance);
        }
    }
}
