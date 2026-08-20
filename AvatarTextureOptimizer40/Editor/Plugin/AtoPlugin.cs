using nadena.dev.ndmf;
using nadena.dev.ndmf.fluent;

[assembly: ExportsPlugin(typeof(Fosa.Ato.Editor.Plugin.AtoPlugin))]

namespace Fosa.Ato.Editor.Plugin
{
    /// <summary>
    /// NDMF plugin entry. We run in the Optimizing phase, AFTER Modular Avatar (which resolves in
    /// Resolving/Transforming) and BEFORE Avatar Optimizer (whose heavy mesh work runs late in
    /// Optimizing). This matches the spec: process after MA, before AAO; AAO's
    /// UVUsageCompabilityAPI is used to evacuate UV channels AAO needs.
    /// NDMF 插件入口。在 Optimizing 阶段执行：MA（Resolving/Transforming）之后、AAO（Optimizing 后期）
    /// 之前。通过 AAO 的 UVUsageCompabilityAPI 疏散 AAO 需要的 UV 通道。
    /// </summary>
    internal sealed class AtoPlugin : Plugin<AtoPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";
        // A brand color for NDMF UI / NDMF UI 主题色
        public override UnityEngine.Color? ThemeColor => new UnityEngine.Color(0.35f, 0.62f, 0.95f);

        protected override void Configure()
        {
            // InPhase(...).AfterPlugin(...).BeforePlugin(...) build up ordering constraints. Missing
            // optional plugins are handled gracefully via a try/catch so ATO works without them.
            // InPhase(...).AfterPlugin(...).BeforePlugin(...) 建立顺序约束；可选依赖缺失时通过
            // try/catch 优雅处理，未安装也能运行。
            Sequence seq;
            try
            {
                seq = InPhase(BuildPhase.Optimizing)
                    .AfterPlugin("nadena.dev.modular-avatar")
                    .BeforePlugin("com.anatawa12.avatar-optimizer");
            }
            catch
            {
                seq = InPhase(BuildPhase.Optimizing);
            }

            seq.Run("Avatar Texture Optimizer", ctx =>
            {
                var pipeline = new Pipeline.AtoPipeline();
                pipeline.Run(ctx);
            });
        }
    }
}
