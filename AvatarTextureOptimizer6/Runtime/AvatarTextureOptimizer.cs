using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer
{
    /// <summary>
    /// AvatarTextureOptimizer 组件。挂载在带有 VRCAvatarDescriptor 的对象上，整个 Avatar 只允许一个。
    /// The ATO component. Attach it to the object that also carries a VRC_AvatarDescriptor.
    /// Only one instance is allowed per avatar (including children).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Avatar Texture Optimizer/Avatar Texture Optimizer")]
    public class AvatarTextureOptimizer : MonoBehaviour
    {
        // ------------------------------------------------------------------
        // 基础选项
        // ------------------------------------------------------------------

        /// <summary>是否生成图集。不勾选 → 不生成图集、不剔除未使用 UV、不重排 UV，直接缩放整张贴图。</summary>
        [Tooltip("Generate texture atlases. When disabled, textures are rescaled as whole images instead of island packing.")]
        public bool generateAtlases = true;

        /// <summary>质量挡位。</summary>
        public ATOQualityPreset qualityPreset = ATOQualityPreset.Balanced;

        /// <summary>Custom 挡位的阈值参数（默认全部 1 = 近无损，不会被其他挡位覆盖）。</summary>
        public QualityThresholds customQuality = new QualityThresholds();

        /// <summary>最小像素密度 px/m（防止发糊）。可选 512/1024/2048/4096/8192。</summary>
        public int minPixelsPerMeter = 2048;

        /// <summary>最大像素密度 px/m（防止浪费）。可选 512/1024/2048/4096/8192。</summary>
        public int maxPixelsPerMeter = 4096;

        // ------------------------------------------------------------------
        // 图集
        // ------------------------------------------------------------------

        /// <summary>实验性 NPOT 图集分辨率（64px 步进）。NPOT 时剔除不支持的格式（如 PVRTC）。</summary>
        public bool npotEnabled = false;

        /// <summary>图集最小 padding（4/8/16/32/64，实际 padding = max(此值, ceil(图集最大边/128))）。</summary>
        public int minPadding = 4;

        // ------------------------------------------------------------------
        // 压缩 / Mip
        // ------------------------------------------------------------------

        public CompressionSettings compression = new CompressionSettings();

        /// <summary>Mipmap 与 MipStreaming 的绑定开关（每类别）。VRChat 要求 Mipmap 开 ⇒ MipStreaming 开。</summary>
        public MipmapSettings mipmaps = new MipmapSettings();

        // ------------------------------------------------------------------
        // 平台覆盖
        // ------------------------------------------------------------------

        public PlatformOverrides platformOverrides = new PlatformOverrides();

        // ------------------------------------------------------------------
        // 白名单
        // ------------------------------------------------------------------

        /// <summary>
        /// 白名单：不限制对象类型（网格/材质/贴图/动画/GameObject 等）。
        /// 白名单对象引用到的全部贴图跳过所有优化（含导入参数优化）；
        /// 同 UV 的其他贴图跳过图集化，但仍参与整图缩放与导入参数优化。
        /// </summary>
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        // ------------------------------------------------------------------
        // 去重
        // ------------------------------------------------------------------

        public bool deduplicateTextures = true;
        public bool deduplicateMaterials = true;

        /// <summary>同一网格上可判定为相同的不透明材质（且动画不单独切换其中之一）时合并材质槽。</summary>
        public bool mergeIdenticalMaterialSlots = true;

        // ------------------------------------------------------------------
        // 性能 / 调试
        // ------------------------------------------------------------------

        public bool useGPUAcceleration = true;
        public bool useBurstJobs = true;

        /// <summary>详细日志（[ATO] 前缀，含每步耗时）。</summary>
        public bool verboseLogging = false;

        // ------------------------------------------------------------------
        // i18n
        // ------------------------------------------------------------------

        /// <summary>界面语言代码（"" = Auto 跟随 NDMF；"en-US"/"zh-CN" 或用户扩展语言代码）。</summary>
        public string language = "";

        // ------------------------------------------------------------------
        // 编辑器辅助（不参与烘焙）
        // ------------------------------------------------------------------

        [HideInInspector] public bool showAdvancedOptions = false;
        [HideInInspector] public bool showQualityOptions = false;
        [HideInInspector] public bool showCompressionOptions = false;
        [HideInInspector] public bool showPlatformOverrides = false;

        // ==================================================================
        // 运行时解析辅助
        // ==================================================================

        /// <summary>
        /// 把自定义挡位的阈值规范化（如未初始化则用近无损值，符合"自定义挡位默认全部为 1"）。
        /// </summary>
        public QualityThresholds GetEffectiveQuality()
        {
            if (qualityPreset == ATOQualityPreset.Custom)
            {
                if (customQuality == null) customQuality = QualityThresholds.NearLossless();
                return customQuality.Clone();
            }
            return QualityThresholds.ForPreset(qualityPreset);
        }

        /// <summary>组件添加时初始化：自定义挡位默认近无损。</summary>
        private void Reset()
        {
            customQuality = QualityThresholds.NearLossless();
        }

        /// <summary>
        /// 校验设置合法性，返回错误信息（空串表示 OK）。烘焙前调用。
        /// </summary>
        public string ValidateSettings()
        {
            if (minPixelsPerMeter > maxPixelsPerMeter)
                return "minPixelsPerMeter must not be greater than maxPixelsPerMeter (最小像素密度不能大于最大像素密度)";
            if (minPadding != 4 && minPadding != 8 && minPadding != 16 && minPadding != 32 && minPadding != 64)
                return "minPadding must be one of 4/8/16/32/64";
            if (!IsPowerOfTwo(minPixelsPerMeter) || !IsPowerOfTwo(maxPixelsPerMeter))
                return "pixels-per-meter values must be powers of two (512/1024/2048/4096/8192)";
            return "";
        }

        private static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;
    }
}
