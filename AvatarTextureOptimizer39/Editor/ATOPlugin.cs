// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using AvatarTextureOptimizer.Editor.Passes;
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(AvatarTextureOptimizer.Editor.ATOPlugin))]

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// NDMF plugin entry point. Registers the ATO pass sequence.
    ///
    /// Ordering:
    ///  - Phase: Optimizing (runs after Modular Avatar's Transforming phase).
    ///  - BeforePlugin("com.anatawa12.avatar-optimizer") → runs before AAO.
    ///
    /// NDMF 插件入口，注册 ATO 的 Pass 序列。
    /// 顺序：
    ///  - 阶段：Optimizing（在 Modular Avatar 的 Transforming 阶段之后）。
    ///  - BeforePlugin("com.anatawa12.avatar-optimizer") → 在 AAO 之前运行。
    /// </summary>
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer (ATO)";

        protected override void Configure()
        {
            // Run before Avatar Optimizer. 在 Avatar Optimizer 之前运行。
            var seq = InPhase(BuildPhase.Optimizing);

            seq.Run(ATOValidateComponentPass.Instance)
                .Then.Run(ATOCollectPass.Instance)
                .Then.Run(ATOShaderAnalysisPass.Instance)
                .Then.Run(ATODeduplicateTexturesPass.Instance)
                .Then.Run(ATOExtractIslandsPass.Instance)
                .Then.Run(ATOScaleIslandsPass.Instance)
                .Then.Run(ATOPackAtlasesPass.Instance)
                .Then.Run(ATORegenerateTexturesPass.Instance)
                .Then.Run(ATORewriteReferencesPass.Instance)
                .Then.Run(ATOReportPass.Instance)
                .BeforePlugin("com.anatawa12.avatar-optimizer");
        }

        protected override void OnUnhandledException(System.Exception e)
        {
            nadena.dev.ndmf.ErrorReport.ReportException(e);
        }
    }
}
