// Copyright (c) fosa. Licensed under the MIT License.
// NDMF plugin registration. ATO runs in the Optimizing phase, after Modular Avatar has finished
// assembling the avatar and before Avatar Optimizer starts removing geometry.
// NDMF 插件注册。ATO 在 Optimizing 阶段运行，
// 位于 Modular Avatar 完成 Avatar 组装之后、Avatar Optimizer 开始移除几何体之前。

using nadena.dev.ndmf;
using Net.Fosa.AvatarTextureOptimizer.Editor;

[assembly: ExportsPlugin(typeof(ATOPlugin))]

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Registers the texture optimization pass with NDMF.
    /// 向 NDMF 注册贴图优化 pass。
    /// </summary>
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        /// <summary>Stable plugin identifier. / 稳定的插件标识符。</summary>
        public const string PluginId = "net.fosa.avatar-texture-optimizer";

        /// <summary>Modular Avatar's plugin id, which must run first. / Modular Avatar 的插件 id，必须先于本插件运行。</summary>
        private const string ModularAvatarId = "nadena.dev.modular-avatar";

        /// <summary>Avatar Optimizer's plugin id, which must run after. / Avatar Optimizer 的插件 id，必须后于本插件运行。</summary>
        private const string AvatarOptimizerId = "com.anatawa12.avatar-optimizer";

        /// <inheritdoc />
        public override string QualifiedName => PluginId;

        /// <inheritdoc />
        public override string DisplayName => "Avatar Texture Optimizer";

        /// <inheritdoc />
        protected override void Configure()
        {
            // Ordering is critical:
            // - after Modular Avatar, so every mesh and material the avatar will actually ship
            //   with already exists;
            // - before Avatar Optimizer, so it can still remove geometry and merge meshes using
            //   the UVs we produced, via the UV usage compatibility API.
            // 顺序至关重要：
            // - 在 Modular Avatar 之后，使 Avatar 最终会使用的所有网格与材质均已存在；
            // - 在 Avatar Optimizer 之前，使其仍能通过 UV 兼容 API
            //   基于我们生成的 UV 移除几何体并合并网格。
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin(ModularAvatarId)
                .BeforePlugin(AvatarOptimizerId)
                .Run(ATOPass.Instance);
        }
    }
}
