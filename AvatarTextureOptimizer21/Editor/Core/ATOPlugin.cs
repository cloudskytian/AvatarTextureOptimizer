// ATO Plugin Registration for NDMF
// ATO 插件注册 (NDMF)
//
// Registers the AvatarTextureOptimizer plugin with the NDMF build pipeline.
// 将AvatarTextureOptimizer插件注册到NDMF构建管线中。

using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.Editor.Core.Passes;

namespace net.fosa.avatar_texture_optimizer.Editor.Core
{
    /// <summary>
    /// NDMF Plugin definition for Avatar Texture Optimizer.
    /// Avatar贴图优化器的NDMF插件定义。
    /// </summary>
    /// <summary>
    /// NDMF Plugin. Explicitly does NOT support NDMF preview (暂不支持ndmf预览).
    /// No PreviewingWith() calls are made on any pass.
    /// </summary>
    public class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Configure()
        {
            // All passes run in the Optimizing phase (after MA, before AAO)
            // 所有Pass在Optimizing阶段运行（MA之后，AAO之前）
            var seq = InPhase(BuildPhase.Optimizing);

            // Step 1: Validation - check component placement, VRC descriptor, etc.
            // 步骤1：验证 - 检查组件位置、VRC描述符等
            seq.Run(ValidationPass.Instance);

            // Step 2: Analyze materials, animations, shaders → build UV-Texture mapping
            // 步骤2：分析材质、动画、着色器 → 建立UV-贴图映射
            seq.Then.Run(AnalysisPass.Instance);

            // Step 3: Deduplicate textures by content + import settings
            // 步骤3：按内容和导入设置对贴图去重
            seq.Then.Run(DeduplicationPass.Instance);

            // Step 4: Evaluate quality per UV island with binary search
            // 步骤4：使用二分搜索评估每个UV岛的质量
            seq.Then.Run(QualityEvaluationPass.Instance);

            // Step 5: UV processing - scaling, atlas packing, UV reassignment
            // 步骤5：UV处理 - 缩放、图集装箱、UV重分配
            seq.Then.Run(UVProcessingPass.Instance);

            // Step 6: Apply changes to meshes, materials, animations
            // 步骤6：将变更应用到网格、材质、动画
            seq.Then.Run(ApplicationPass.Instance);

            // Step 7: Post-process - material/texture dedup, AAO compat, cleanup, report
            // 步骤7：后处理 - 材质/贴图去重、AAO兼容、清理、报告
            seq.Then.Run(PostProcessPass.Instance);
        }
    }
}
