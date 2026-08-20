using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(Fosa.ATO.Editor.ATOPlugin))]

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// ATO 的 NDMF 插件。所有 Pass 运行在 Optimizing 相位（MA 之后、AAO 之前）。
    /// ATO NDMF plugin. All passes run in the Optimizing phase (after MA, before AAO).
    /// </summary>
    public class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer (ATO)";

        protected override void Configure()
        {
            // 运行在 Optimizing：自然排在 MA（Generating/Transforming）之后；
            // 显式约束在 AAO（com.anatawa12.avatar-optimizer）之前。
            InPhase(BuildPhase.Optimizing)
                .Run(ATOCollectPass.Instance)
                .Then.Run(ATOAnalyzePass.Instance)
                .Then.Run(ATOProcessPass.Instance)
                .Then.Run(ATOPackPass.Instance)
                .Then.Run(ATOApplyPass.Instance)
                .BeforePlugin("com.anatawa12.avatar-optimizer");
        }

        protected override void OnUnhandledException(System.Exception e)
        {
            Debug.LogError($"[ATO] Unhandled exception: {e}");
        }
    }
}
