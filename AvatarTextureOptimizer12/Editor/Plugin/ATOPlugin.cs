// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - NDMF plugin definition.
// AvatarTextureOptimizer (ATO) - NDMF 插件定义。

using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Net.Fosa.AvatarTextureOptimizer.Editor.Plugin;
using UnityEngine;

[assembly: ExportsPlugin(typeof(ATOPlugin))]

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Plugin
{
    /// <summary>
    /// EN: The plugin runs in the Optimizing phase, which is after Modular Avatar (Transforming), and is
    ///     explicitly ordered before AAO so that AAO sees our final meshes and can still use its own UV
    ///     evacuation mechanism.
    /// ZH: 插件运行于 Optimizing 阶段，即 Modular Avatar（Transforming）之后，
    ///     并显式排在 AAO 之前，使 AAO 能看到我们的最终网格，同时仍可使用它自己的 UV 迁移机制。
    /// </summary>
    [RunsOnAllPlatforms]
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        /// <summary>EN: AAO's NDMF plugin id, taken from its own source. ZH: AAO 的 NDMF 插件 id，取自其源码。</summary>
        public const string AAOQualifiedName = "com.anatawa12.avatar-optimizer";

        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";
        public override Color? ThemeColor => new Color(0.30f, 0.72f, 0.96f);

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                {
                    seq.Run(ATOMainPass.Instance)
                        .BeforePlugin(AAOQualifiedName);
                });
        }
    }
}
