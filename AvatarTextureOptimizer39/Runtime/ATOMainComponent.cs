// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarTextureOptimizer
{
    /// <summary>
    /// Main component. Attach exactly one of these to the avatar root (an object that
    /// has a VRCAvatarDescriptor) to optimize the whole avatar.
    ///
    /// 主组件。挂到 Avatar 根节点（必须带有 VRCAvatarDescriptor 的对象）以优化整个 Avatar。
    /// 一个 Avatar 及其子级上只允许挂载一个。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Avatar Texture Optimizer/ATO Avatar Optimizer")]
    public class ATOAvatarTextureOptimizer : MonoBehaviour
    {
        [Header("General / 通用")]
        [Tooltip("Generate atlases. When disabled, no atlas is built: unused UVs are kept, " +
                 "UV layout is not reordered, and textures are simply scaled. / " +
                 "是否生成图集。关闭时：不剔除未使用 UV、不重排 UV，直接缩放整张贴图。")]
        public bool generateAtlas = true;

        [Tooltip("Minimum pixel density (px per meter). Islands are clamped to at least this. / " +
                 "最小像素密度（px/m）。岛缩放后不低于该密度。")]
        public ATOPixelDensityPreset minPixelDensity = ATOPixelDensityPreset.Px2048;

        [Tooltip("Maximum pixel density (px per meter). Islands are clamped to at most this. / " +
                 "最大像素密度（px/m）。岛缩放后不高于该密度。")]
        public ATOPixelDensityPreset maxPixelDensity = ATOPixelDensityPreset.Px4096;

        [Tooltip("Quality settings (preset + custom thresholds). / 质量设置（挡位 + 自定义阈值）。")]
        public ATOQualitySettings quality = new ATOQualitySettings();

        [Tooltip("Minimum padding between islands in an atlas. / 图集岛间最小 padding。")]
        public ATOAtlasPadding atlasPadding = ATOAtlasPadding.Px4;

        [Tooltip("Experimental NPOT atlas sizes (64px step). May not be supported by all " +
                 "compression formats. / 实验性 NPOT 图集边长（64px 步进）。部分压缩格式不支持。")]
        public bool allowNPOT = false;

        [Header("Texture compression / 贴图压缩")]
        [Tooltip("Compression & mip settings per texture category. / 按贴图类别的压缩与 mip 设置。")]
        public ATOCompressionSettings compression = new ATOCompressionSettings();

        [Header("Platform override / 平台覆盖")]
        [Tooltip("Per-platform overrides (PC / Android / iOS). / 各平台覆盖（PC/Android/iOS）。")]
        public ATOPlatformOverride platformOverride = new ATOPlatformOverride();

        [Header("Whitelist / 白名单")]
        [Tooltip("Objects referenced here (meshes, materials, textures, animations, ...) cause ALL " +
                 "textures they reference to skip every optimization. / " +
                 "此处引用的对象（网格/材质/贴图/动画…）会使其引用的全部贴图跳过所有优化。")]
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        [Header("Material & texture deduplication / 材质与贴图去重")]
        [Tooltip("Deduplicate identical materials after optimization. / 优化后对完全相同的材质去重。")]
        public bool deduplicateMaterials = true;

        [Tooltip("Deduplicate identical textures/atlases after optimization. / 优化后对完全相同的贴图/图集去重。")]
        public bool deduplicateTextures = true;

        [Header("Advanced / 高级")]
        [Tooltip("Bake progress reporting verbosity. 0=quiet, 1=normal, 2=verbose (per-step timing). / " +
                 "烘焙进度日志级别。0=安静，1=正常，2=详细（逐步计时）。")]
        [Range(0, 2)] public int logLevel = 1;
    }
}
