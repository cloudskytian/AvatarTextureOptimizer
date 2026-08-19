using nadena.dev.ndmf;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

[assembly: ExportsPlugin(typeof(FOSA.AvatarTextureOptimizer.Editor.ATOPlugin))]

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// NDMF plugin. Runs in Optimizing, after Modular Avatar (and TTT if present), before AAO.
    /// NDMF 插件。Optimizing 阶段，MA（以及若存在的 TTT）之后、AAO 之前。
    /// </summary>
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => AvatarTextureOptimizer.PackageName;
        public override string DisplayName => "Avatar Texture Optimizer";
        public override Color? ThemeColor => new Color(0.55f, 0.35f, 0.85f, 1f);

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("net.rs64.tex-trans-tool")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run(ATOOptimizePass.Instance)
                .Then.Run(ATOCleanupPass.Instance);
        }

        protected override void OnUnhandledException(System.Exception e)
        {
            if (e is ATOCanceledException)
            {
                Debug.Log($"{AvatarTextureOptimizer.LogPrefix} Canceled by user.");
                return;
            }
            ErrorReport.ReportException(e);
        }
    }
}
