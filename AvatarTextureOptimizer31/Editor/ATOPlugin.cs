// ATOPlugin.cs
// NDMF plugin registration. Runs in the Transforming phase, after Modular Avatar
// (QualifiedName "net.fosa..." sorts after "nadena.dev.modular-avatar") and before
// Avatar Optimizer (which runs in the Optimizing phase).
// NDMF 插件注册。在 Transforming 阶段运行，MA 之后、AAO（Optimizing 阶段）之前。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using nadena.dev.ndmf;
using nadena.dev.ndmf.fluent;

[assembly: ExportsPlugin(typeof(Fosa.AvatarTextureOptimizer.ATOPlugin))]

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Registers ATO's pipeline with NDMF. All processing happens in a single comprehensive pass
    /// in the Transforming phase, ensuring we run after Modular Avatar's main logic and before
    /// Avatar Optimizer's optimization phase.
    /// 向 NDMF 注册 ATO 管线。所有处理在 Transforming 阶段的一个综合 Pass 中完成。
    /// </summary>
    internal sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Configure()
        {
            // Run in Transforming phase. NDMF sorts plugins within a phase by QualifiedName,
            // so "net.fosa..." runs after "nadena.dev.modular-avatar" (alphabetically).
            // AAO runs in Optimizing phase (later), so we are correctly before AAO.
            InPhase(BuildPhase.Transforming)
                .Run("ATO: Texture Optimization Pipeline", ctx =>
                {
                    var pipeline = new Core.ATOPipeline(ctx);
                    pipeline.Execute();
                });
        }
    }
}
