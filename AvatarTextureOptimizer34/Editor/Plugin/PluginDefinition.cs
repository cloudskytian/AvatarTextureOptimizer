// AvatarTextureOptimizer - PluginDefinition
// EN: NDMF plugin registration. Runs in the Transforming phase, after Modular Avatar, before AAO.
// CN: NDMF 插件注册。在 Transforming 阶段运行：MA 之后、AAO 之前。
using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.Plugin;

[assembly: ExportsPlugin(typeof(AtoPlugin))]

namespace net.fosa.avatar_texture_optimizer.Plugin
{
    /// <summary>
    /// EN: ATO plugin. See Configure() for phase & ordering constraints.
    /// CN: ATO 插件。阶段与顺序约束见 Configure()。
    /// </summary>
    [RunsOnAllPlatforms]
    public class AtoPlugin : Plugin<AtoPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => I18n.T("plugin.display");

        protected override void Configure()
        {
            // EN: After MA (both main and late-transform stages), before AAO. Verified against NDMF 1.14.4 + MA 1.18.2 + AAO 1.9.17 sources.
            // CN: 在 MA（主阶段与 late-transform 阶段）之后、AAO 之前。已对照 NDMF 1.14.4 + MA 1.18.2 + AAO 1.9.17 源码核实。
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("nadena.dev.modular-avatar.late-transform-stages")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run("ATO: AvatarTextureOptimizer", ctx => new AtoBuildPass().Execute(ctx));
        }
    }
}
