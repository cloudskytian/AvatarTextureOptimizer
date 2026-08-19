// AvatarTextureOptimizer
// File: Editor/NDMF/ATOPlugin.cs
//
// NDMF plugin registration. The whole pipeline runs in the Optimizing phase
// AFTER Modular Avatar (MA's passes mostly run in Transforming) and BEFORE
// Avatar Optimizer (AAO), as required. A weak BeforePlugin constraint is used
// against AAO's qualified name so the tool works even when AAO is not
// installed.
//
// NDMF 插件注册。整个流水线运行在 Optimizing 阶段，位于 Modular Avatar
// （MA 的 pass 大多运行在 Transforming）之后、Avatar Optimizer（AAO）之前，
// 符合要求。对 AAO 的限定名使用弱顺序约束，因此即使未安装 AAO 本工具
// 也能正常工作。

using net.fosa.avatar_texture_optimizer.editor.ndmf.passes;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(net.fosa.avatar_texture_optimizer.editor.ndmf.ATOPlugin))]

namespace net.fosa.avatar_texture_optimizer.editor.ndmf
{
    /// <summary>
    /// NDMF plugin entry point. / NDMF 插件入口。
    /// </summary>
    [RunsOnAllPlatforms]
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer (ATO)";
        public override Color? ThemeColor => new Color(0.29f, 0.62f, 0.86f);

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                // Validate component placement first.
                // 首先校验组件挂载是否合规。
                .Run(ATOValidatePass.Instance)

                // Analysis: collect usages, animations, whitelist, dedup.
                // 分析：收集引用、动画、白名单、去重。
                .Then.Run(ATOCollectPass.Instance)

                // Build UV groups (per UV space) and type groups (packing).
                // 构建 UV 组（按 UV 空间）与类型组（装箱）。
                .Then.Run(ATOGroupPass.Instance)

                // Extract UV islands from the meshes.
                // 从网格提取 UV 岛。
                .Then.Run(ATOExtractIslandsPass.Instance)

                // Quality-scale islands (or whole textures without atlas).
                // 质量缩放岛（或未图集化时的整张贴图）。
                .Then.Run(ATOScalePass.Instance)

                // Pack islands into atlas layout (Burst raster + BLF).
                // 将岛装箱进图集布局（Burst 光栅化 + BLF）。
                .Then.Run(ATOPackPass.Instance)

                // Create the actual atlas textures (with pull-push fill).
                // 创建实际图集贴图（含 pull-push 填充）。
                .Then.Run(ATOBuildAtlasesPass.Instance)

                // Apply to meshes, materials and animations.
                // 应用到网格、材质与动画。
                .Then.Run(ATOApplyPass.Instance)

                // Final dedup of materials, remove our component, print report.
                // 材质最终去重、移除自身组件、输出报告。
                .Then.Run(ATOFinalizePass.Instance)

                // Run before AAO so AAO sees the optimized result.
                // 在 AAO 之前运行，使 AAO 看到优化后的结果。
                .BeforePlugin("com.anatawa12.avatar-optimizer");
        }
    }
}
