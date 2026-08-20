using System.Collections.Generic;
using UnityEngine;

namespace Fosa.ATO
{
    /// <summary>
    /// Avatar Texture Optimizer（ATO）组件。
    /// 挂载到 Avatar 根物体（必须带 VRCAvatarDescriptor），一个 Avatar 及其子级只允许挂载一个。
    /// 挂在同一 Avatar 上的全部子级物体会被整体分析优化。
    ///
    /// Avatar Texture Optimizer component. Attach to the avatar root (must have a VRCAvatarDescriptor).
    /// Only one instance is allowed per avatar (including children).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Fosa/Avatar Texture Optimizer")]
    public class AvatarTextureOptimizer : MonoBehaviour
    {
        [Header("General / 通用")]
        [Tooltip("是否生成图集。关闭则不生成图集、不剔除未使用 UV、不重排 UV，只做整贴图缩放与其他优化。\nGenerate atlases. When off: no atlas, no unused-UV culling, no UV repack; only whole-texture scaling and other optimizations.")]
        public bool generateAtlas = true;

        [Tooltip("图集 padding 挡位（岛间距，px）。Atlas island padding preset.")]
        public ATOPadding padding = ATOPadding.P4;

        [Tooltip("实验性 NPOT 图集分辨率（64 步进）。已验证支持 MipStreaming/Crunch。Experimental NPOT atlas resolution (64px step).")]
        public bool allowNPOT = false;

        [Header("Quality / 质量")]
        [Tooltip("目标质量挡位。Target quality preset.")]
        public ATOQualityPreset qualityPreset = ATOQualityPreset.Medium;

        [Tooltip("自定义质量参数（仅 Custom 挡位生效，不被其他挡位覆盖）。Custom quality params (only for Custom preset).")]
        public ATOQualityParams customQuality = ATOQualityParams.FromPreset(ATOQualityPreset.Custom);

        [Header("Pixel Density / 像素密度")]
        [Tooltip("最小像素密度（px/米）。Minimum pixel density (px per meter).")]
        public float minPixelDensity = 2048f;

        [Tooltip("最大像素密度（px/米）。Maximum pixel density (px per meter).")]
        public float maxPixelDensity = 4096f;

        [Header("Mipmap & Streaming / 多级与流式")]
        [Tooltip("同时控制 Mipmap 与 MipStreaming（VRChat 要求开 Mipmap 必须开 MipStreaming，二者绑定）。\nControls both mipmaps and mip streaming (VRChat requires streaming when mipmaps are on).")]
        public bool mipmapAndStreaming = true;

        [Header("Deduplication / 去重")]
        [Tooltip("对内容/参数完全相同的材质进行去重并合并材质槽。Deduplicate identical materials and merge slots.")]
        public bool dedupMaterials = true;

        [Tooltip("对内容/参数完全相同的贴图/图集进行去重。Deduplicate identical textures/atlases.")]
        public bool dedupTextures = true;

        [Header("Compression / 压缩")]
        public ATOCompressionSettings compression = new ATOCompressionSettings();

        [Header("Platform Override / 平台覆盖")]
        [Tooltip("按平台分别覆盖优化参数（PC/Android/iOS）。Per-platform overrides.")]
        public ATOPlatformSettings platformPC = new ATOPlatformSettings();
        public ATOPlatformSettings platformAndroid = new ATOPlatformSettings { maxAtlasSize = 4096 };
        public ATOPlatformSettings platformiOS = new ATOPlatformSettings { maxAtlasSize = 4096 };

        [Header("Whitelist / 白名单")]
        [Tooltip("白名单对象（不限类型：网格/材质/贴图/动画/GameObject 等）。白名单内对象引用的全部贴图跳过所有优化。\nWhitelisted objects (any type). All textures referenced by whitelisted objects skip ALL optimization.")]
        public List<Object> whitelist = new List<Object>();

        [Header("Advanced / 高级")]
        [Tooltip("详细日志开关（含每步耗时）。Verbose [ATO] logging.")]
        public bool verboseLogging = false;

        /// <summary>获取当前生效的质量参数（Custom 挡位用用户自定义值，否则用预设值）。</summary>
        public ATOQualityParams GetEffectiveQualityParams()
        {
            if (qualityPreset == ATOQualityPreset.Custom)
                return customQuality ?? (customQuality = ATOQualityParams.FromPreset(ATOQualityPreset.Custom));
            return ATOQualityParams.FromPreset(qualityPreset);
        }
    }
}
