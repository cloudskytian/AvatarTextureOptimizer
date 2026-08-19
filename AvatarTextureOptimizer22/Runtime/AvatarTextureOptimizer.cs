// AvatarTextureOptimizer
// Component: AvatarTextureOptimizer
// 组件：AvatarTextureOptimizer
//
// The runtime component of the tool. The user drops this on the avatar root
// (the GameObject that carries VRCAvatarDescriptor). All optimization settings
// are stored here as serialized fields, so both novice and advanced users can
// configure the tool without writing any code.
//
// 本工具的运行期组件。用户将其挂载到 Avatar 根对象（带 VRCAvatarDescriptor 的
// GameObject）上。所有优化设置都以序列化字段的形式保存在此组件上，使新手与
// 高级用户都能在不写代码的情况下完成配置。

using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// The main component of Avatar Texture Optimizer.
    /// One instance per avatar (root + children combined); the attached object must
    /// carry a VRCAvatarDescriptor. The NDMF build pass consumes this component,
    /// performs the optimization, and removes itself from the baked result.
    ///
    /// Avatar Texture Optimizer 的主组件。每个 Avatar（含子级）只允许挂载一个；
    /// 挂载对象上必须存在 VRCAvatarDescriptor。NDMF 烘焙 pass 读取本组件、
    /// 执行优化，并在烘焙完成后将自身从成品上移除。
    /// </summary>
    [AddComponentMenu("Avatar Texture Optimizer/Avatar Texture Optimizer")]
    [DisallowMultipleComponent]
    public sealed class AvatarTextureOptimizer : MonoBehaviour
    {
        // ---- Settings container ----
        // 设置容器
        [Tooltip("Master switch for the whole tool. / 整个工具的总开关。")]
        public bool Enabled = true;

        [Tooltip("Generate texture atlases. When disabled, textures are scaled directly without UV remapping or atlas packing. / 是否生成图集。关闭时不生成图集、不剔除未使用 UV、不重排 UV，直接缩放贴图。")]
        public bool GenerateAtlas = true;

        [Tooltip("Optimize materials (deduplicate identical materials, merge material slots). / 是否优化材质（对相同材质去重、合并材质槽）。")]
        public bool OptimizeMaterials = true;

        [Tooltip("Optimize textures / atlases (deduplicate identical textures, adjust import settings). / 是否优化贴图/图集（去重、调整导入参数）。")]
        public bool OptimizeTextures = true;

        [Tooltip("Quality configuration. / 质量配置。")]
        public QualitySettings Quality = new QualitySettings();

        [Tooltip("Atlas generation configuration. / 图集生成配置。")]
        public AtlasSettings Atlas = new AtlasSettings();

        [Tooltip("Texture import settings applied to generated atlases and fallback textures. / 应用到生成的图集与 fallback 贴图上的导入参数。")]
        public ImportSettings Import = new ImportSettings();

        [Tooltip("Platform-specific overrides (PC / Android / iOS). / 平台特定覆写设置。")]
        public PlatformSettings Platforms = new PlatformSettings();

        [Tooltip("Objects whose referenced textures are excluded from ALL optimization. / 白名单：其引用的全部贴图跳过所有优化。")]
        public WhitelistSettings Whitelist = new WhitelistSettings();

        [Tooltip("UI language. Auto = follow NDMF's current language. / 界面语言。Auto = 跟随 NDMF 当前语言。")]
        public string Locale = "Auto";

        [Tooltip("Enable verbose [ATO] logging for debugging. / 是否输出详细 [ATO] 日志用于调试。")]
        public bool VerboseLogging = false;

        /// <summary>
        /// Validation for editors and build-time checks.
        /// Returns null when valid, otherwise an error description.
        /// The authoritative descriptor check happens in the NDMF validate
        /// pass (ATOValidatePass) where the real VRCSDK API is available;
        /// this method is a light sanity hook only.
        /// 供编辑器与构建时校验使用。合法时返回 null，否则返回错误描述。
        /// 权威的描述符检查在 NDMF 校验 pass（ATOValidatePass）中通过真实
        /// VRCSDK API 完成；本方法仅作轻量健全性挂钩。
        /// </summary>
        public string Validate()
        {
            // The Runtime assembly intentionally has no VRCSDK dependency;
            // ATOValidatePass performs the real check with VRCAvatarDescriptor.
            // Runtime 程序集刻意不依赖 VRCSDK；真实的检查由 ATOValidatePass
            // 使用 VRCAvatarDescriptor 完成。
            return null;
        }
    }
}
