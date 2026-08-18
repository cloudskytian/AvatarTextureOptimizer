// Avatar Texture Optimizer (ATO)
// NDMF plugin declaration. / NDMF 插件声明。
//
// Ordering rationale (verified against NDMF 1.14.4 source):
//   - Modular Avatar performs most of its work in BuildPhase.Transforming.
//   - AAO's main sequence runs in BuildPhase.Optimizing (QualifiedName "com.anatawa12.avatar-optimizer").
//   - We run in BuildPhase.Optimizing and declare BeforePlugin(AAO), so we execute
//     strictly after MA and strictly before AAO. BeforePlugin is a no-op if AAO is absent.
// 顺序说明（已对照 NDMF 1.14.4 源码验证）：
//   - Modular Avatar 主要工作在 Transforming 阶段。
//   - AAO 主序列在 Optimizing 阶段（插件标识 com.anatawa12.avatar-optimizer）。
//   - 我们在 Optimizing 阶段运行并声明 BeforePlugin(AAO)，从而严格在 MA 之后、AAO 之前执行；
//     AAO 未安装时 BeforePlugin 安全无副作用。

using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(NetFosa.ATO.ATOPlugin))]

namespace NetFosa.ATO
{
    /// <summary>
    /// NDMF plugin entry point. / NDMF 插件入口。
    /// </summary>
    public class ATOPlugin : Plugin<ATOPlugin>
    {
        /// <summary>Human-readable plugin name. / 可读的插件名称。</summary>
        public override string DisplayName => "ATO: Avatar Texture Optimizer";

        /// <summary>Stable qualified name used for ordering constraints. / 用于顺序约束的稳定标识。</summary>
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";

        protected override void Configure()
        {
            // Run after Modular Avatar (Transforming) and before Avatar Optimizer (Optimizing).
            // 在 Modular Avatar（Transforming）之后、Avatar Optimizer（Optimizing）之前运行。
            InPhase(BuildPhase.Optimizing)
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run("ATO: Optimize textures & UVs", ctx => ATOPass.Execute(ctx));
        }
    }
}
