// ATOPlugin.cs / ATOPlugin.cs
// NDMF plugin definition for Avatar Texture Optimizer.
// Avatar贴图优化器的NDMF插件定义。

using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.Editor.Util;

[assembly: ExportsPlugin(typeof(net.fosa.avatar_texture_optimizer.Editor.ATOPlugin))]

namespace net.fosa.avatar_texture_optimizer.Editor
{
    public class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Configure()
        {
            // Run in Transforming phase AFTER Modular Avatar completes but BEFORE Avatar Optimizer starts.
            // Modular Avatar runs in Transforming; AAO runs in Optimizing.
            // 在Transforming阶段运行：Modular Avatar完成之后，Avatar Optimizer开始之前。
            // MA在Transforming中运行；AAO在Optimizing中运行。
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run(ATOBuildPass.Instance);
        }
    }
}
