// NDMF plugin registration for Avatar Texture Optimizer.
// / ATO 的 NDMF 插件注册。
// Ordering requirements: run AFTER Modular Avatar, BEFORE Avatar Optimizer.
// / 执行顺序要求：MA 之后、AAO 之前。

using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.editor.pipeline;

[assembly: ExportsPlugin(typeof(net.fosa.avatar_texture_optimizer.editor.plugin.AvatarTextureOptimizerPlugin))]

namespace net.fosa.avatar_texture_optimizer.editor.plugin
{
    /// <summary>
    /// NDMF plugin. / NDMF 插件。
    /// The actual work happens in Optimizing phase, after MA and before AAO.
    /// / 实际工作在 Optimizing 阶段，位于 MA 之后、AAO 之前。
    /// </summary>
    [RunsOnAllPlatforms]
    public sealed class AvatarTextureOptimizerPlugin : Plugin<AvatarTextureOptimizerPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer (ATO)";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .Run("Avatar Texture Optimizer", ctx =>
                {
                    // Find our component. If absent, this avatar is simply not managed by ATO. / 查找组件；不存在则本 Avatar 不受 ATO 管理。
                    var comp = ctx.AvatarRootObject.GetComponentInChildren<runtime.AvatarTextureOptimizer>(true);
                    if (comp == null) return;

                    // Validate the mounting rules. / 校验挂载规则（VRCAvatarDescriptor + 单组件限制）。
                    comp.ValidateAvatarSetup();

                    // Run the pipeline. / 运行流水线。
                    PipelineRunner.Run(ctx, comp);
                })
                .AfterPlugin("nadena.dev.modular-avatar")     // runs after MA / MA 之后
                .BeforePlugin("com.anatawa12.avatar-optimizer"); // runs before AAO / AAO 之前
        }
    }
}
