// ATOPlugin.cs - NDMF plugin declaration. Runs AFTER Modular Avatar, BEFORE Avatar Optimizer.
// NDMF插件声明。在 Modular Avatar 之后、Avatar Optimizer 之前执行。
using Fosa.ATO.Editor.Pipeline;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;

[assembly: ExportsPlugin(typeof(Fosa.ATO.Editor.ATOPlugin))]

namespace Fosa.ATO.Editor
{
    public class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => TextureOptimizerPipeline.PluginQualifiedName;
        public override string DisplayName => "Avatar Texture Optimizer";
        public override Color? ThemeColor => new Color(0x2f / 255f, 0xb5 / 255f, 0xa3 / 255f, 1f);

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")   // after MA / MA后
                .BeforePlugin("com.anatawa12.avatar-optimizer") // before AAO / AAO前
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                {
                    seq.Run("Optimize textures", ctx => TextureOptimizerPipeline.Run(ctx));
                });
        }

        protected override void OnUnhandledException(System.Exception e)
        {
            if (e is Core.ATOCancelledException)
            {
                Debug.LogWarning("[ATO] build cancelled by user / 已被用户取消");
                return;
            }
            ErrorReport.ReportException(e);
        }
    }
}
