using nadena.dev.ndmf;
using net.fosa.ato.editor;
using UnityEngine;

[assembly: ExportsPlugin(typeof(ATOPlugin))]

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: NDMF plugin registration.
    ///
    ///     Placement: the pass runs in <see cref="BuildPhase.Optimizing"/> and declares an explicit
    ///     ordering constraint before Avatar Optimizer. Modular Avatar does the bulk of its work in
    ///     <see cref="BuildPhase.Transforming"/>, which NDMF always schedules before Optimizing, so
    ///     "after MA, before AAO" is satisfied without a fragile pass-name dependency on MA.
    ///     Verified against AvatarOptimizer 1.9.17 (OptimizerPlugin.cs) and Modular Avatar 1.18.2.
    ///
    /// ZH: NDMF 插件注册。
    ///
    ///     落位：Pass 运行在 <see cref="BuildPhase.Optimizing"/>，并显式声明排在 Avatar Optimizer 之前。
    ///     Modular Avatar 的主体工作在 <see cref="BuildPhase.Transforming"/>，
    ///     NDMF 总是把它安排在 Optimizing 之前，因此"MA 之后、AAO 之前"无需依赖 MA 的 Pass 名即可满足。
    ///     已对照 AvatarOptimizer 1.9.17（OptimizerPlugin.cs）与 Modular Avatar 1.18.2 核实。
    /// </summary>
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        /// <inheritdoc/>
        public override string QualifiedName => ATOConstants.PluginQualifiedName;

        /// <inheritdoc/>
        public override string DisplayName => ATOConstants.DisplayName;

        /// <inheritdoc/>
        public override Color? ThemeColor => new Color(0.29f, 0.62f, 0.86f);

        /// <inheritdoc/>
        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .WithRequiredExtension(typeof(nadena.dev.ndmf.animator.AnimatorServicesContext), seq =>
                {
                    seq.Run(ATOPass.Instance)
                       .BeforePlugin(ATOConstants.AAOPluginQualifiedName);
                });
        }
    }
}
