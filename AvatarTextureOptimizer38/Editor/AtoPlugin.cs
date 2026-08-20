using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;

[assembly: ExportsPlugin(typeof(Net.Fosa.AvatarTextureOptimizer.Editor.AtoPlugin))]

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// NDMF plugin entry. Runs in Transforming after Modular Avatar (and TTT if present), before AAO Optimizing.
    /// NDMF 插件入口。Transforming 阶段、Modular Avatar（及 TTT）之后、AAO Optimizing 之前。
    /// AfterPlugin on missing plugins is a weak no-op (NDMF skips unknown pass keys).
    /// 未安装的插件上的 AfterPlugin 为弱约束（NDMF 会跳过未知 pass）。
    /// </summary>
    [RunsOnPlatforms(WellKnownPlatforms.VRChatAvatar30)]
    public sealed class AtoPlugin : Plugin<AtoPlugin>
    {
        public override string QualifiedName => AvatarTextureOptimizer.PackageName;
        public override string DisplayName => "Avatar Texture Optimizer";
        public override Color? ThemeColor => new Color(0.95f, 0.55f, 0.15f, 1f);

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("nadena.dev.modular-avatar.late-transform-stages")
                .AfterPlugin("net.rs64.tex-trans-tool")
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                {
                    seq.Run(AtoOptimizePass.Instance);
                });
        }

        protected override void OnUnhandledException(System.Exception e)
        {
            if (e is AtoCanceledException)
            {
                AtoLog.Warn("Bake canceled by user. Temp assets on disk were kept; CPU/GPU/memory released. / 用户取消。磁盘临时资产保留，内存已释放。");
                return;
            }
            ErrorReport.ReportException(e);
            AtoLog.Error($"Unhandled exception: {e}");
        }
    }
}
