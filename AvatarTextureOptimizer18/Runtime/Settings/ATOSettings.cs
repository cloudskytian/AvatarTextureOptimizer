using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    // 质量挡位。Quality preset.
    public enum ATOQualityPreset
    {
        // 高（默认）。High (default).
        High = 0,
        // 中。Medium.
        Medium = 1,
        // 低。Low.
        Low = 2,
        // 超高。Ultra.
        Ultra = 3,
        // 自定义（默认全 1 近无损，参数由用户修改，不会被其他挡位覆盖）。Custom (all-1 near-lossless by default).
        Custom = 4
    }

    // 贴图类别（决定压缩格式、mipmap 与度量方式）。Texture category (drives format, mipmaps and metric choice).
    public enum ATOTextureCategory
    {
        // 不透明颜色。Opaque color.
        OpaqueColor = 0,
        // 含透明颜色（Cutout/Blend）。Color with transparency (cutout/blend).
        AlphaColor = 1,
        // 法线贴图。Normal map.
        NormalMap = 2,
        // 灰度/蒙版贴图。Grayscale / mask.
        Grayscale = 3
    }

    // 压缩格式安全枚举。Safe compression format enumeration.
    public enum ATOCompressionFormat
    {
        // 自动（按平台与类别选择最优）。Automatic (platform & category best choice).
        Auto = 0,
        BC7,
        BC5,
        ETC2_RGBA8,
        ASTC_4x4,
        ASTC_6x6,
        ASTC_8x8,
        ASTC_10x10,
        ASTC_12x12,
        // 仅 iOS。iOS only.
        PVRTC_4BPP_RGBA,
        RGB24,
        RGBA32,
        R8,
        R16,
        RG16,
        RHalf,
        RGHalf
    }

    // 目标平台。Target platform.
    public enum ATOPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2
    }

    // 单类别贴图设置。Per-category texture settings.
    [Serializable]
    public class ATOCategorySettings
    {
        // 压缩格式。Compression format.
        public ATOCompressionFormat format = ATOCompressionFormat.Auto;

        // 是否生成 Mipmap。VRChat 要求开启 Mipmap 时强制开启 MipStreaming，二者绑定，仅此一个开关。
        // Whether to generate mipmaps. VRChat requires MipStreaming when mipmaps are on; the two are bound to this single toggle.
        public bool mipmaps = true;

        public ATOCategorySettings Clone()
        {
            return new ATOCategorySettings { format = format, mipmaps = mipmaps };
        }
    }

    // 四类贴图的格式设置。Format settings for the four texture categories.
    [Serializable]
    public class ATOFormatSettings
    {
        public ATOCategorySettings opaqueColor = new ATOCategorySettings();
        public ATOCategorySettings alphaColor = new ATOCategorySettings();
        public ATOCategorySettings normalMap = new ATOCategorySettings();
        public ATOCategorySettings grayscale = new ATOCategorySettings();

        public ATOCategorySettings For(ATOTextureCategory category)
        {
            switch (category)
            {
                case ATOTextureCategory.AlphaColor: return alphaColor;
                case ATOTextureCategory.NormalMap: return normalMap;
                case ATOTextureCategory.Grayscale: return grayscale;
                default: return opaqueColor;
            }
        }

        public ATOFormatSettings Clone()
        {
            return new ATOFormatSettings
            {
                opaqueColor = opaqueColor.Clone(),
                alphaColor = alphaColor.Clone(),
                normalMap = normalMap.Clone(),
                grayscale = grayscale.Clone()
            };
        }
    }

    // 质量算法各度量阈值。Target quality thresholds for each metric.
    // 阈值依据学术/业内研究设定：
    // - MS-SSIM: Wang et al. 2004 多尺度结构相似度；0.99 以上视觉几乎无损，0.95~0.97 为高质量压缩常见目标。
    // - CIEDE2000: Sharma et al. 2005；ΔE ≤ 2.3 为 JND（刚好可察觉），高质量目标取 ≤ 1.0。
    // - 法线角度 p95 与灰度/alpha RMSE 为业内法线/蒙版重采样实践值。
    [Serializable]
    public class ATOMetricThresholds
    {
        // MS-SSIM 下限（0~1，1 = 无损）。MS-SSIM lower bound (1 = lossless).
        [Range(0f, 1f)] public float msSsim = 0.99f;

        // CIEDE2000 ΔE 上限（0 = 无损）。ΔE upper bound (0 = lossless).
        public float deltaE2000 = 1.0f;

        // Cutout：clip 后轮廓 IoU 下限（1 = 无损）。Cutout: post-clip silhouette IoU lower bound.
        [Range(0f, 1f)] public float alphaIoU = 0.995f;

        // Blend：alpha 线性 RMSE 上限（0 = 无损）。Blend: alpha linear RMSE upper bound.
        public float alphaRMSE = 2f / 255f;

        // 法线：角度误差 p95 上限（度，0 = 无损）。Normal: angle error p95 upper bound in degrees.
        public float normalAngleDegP95 = 1.5f;

        // 灰度：被使用通道的线性 RMSE 上限，逐通道取最差（0 = 无损）。Grayscale: linear RMSE upper bound per used channel.
        public float grayRMSE = 1f / 255f;

        // 近无损（全 1/0），自定义挡位默认值。Near-lossless (all 1/0), the default for the Custom preset.
        public static ATOMetricThresholds Lossless()
        {
            return new ATOMetricThresholds
            {
                msSsim = 1f,
                deltaE2000 = 0f,
                alphaIoU = 1f,
                alphaRMSE = 0f,
                normalAngleDegP95 = 0f,
                grayRMSE = 0f
            };
        }

        // 按挡位返回阈值。Returns thresholds for a preset.
        public static ATOMetricThresholds ForPreset(ATOQualityPreset preset)
        {
            switch (preset)
            {
                case ATOQualityPreset.Ultra:
                    return new ATOMetricThresholds { msSsim = 0.995f, deltaE2000 = 0.5f, alphaIoU = 0.999f, alphaRMSE = 1f / 255f, normalAngleDegP95 = 0.5f, grayRMSE = 0.5f / 255f };
                case ATOQualityPreset.Medium:
                    return new ATOMetricThresholds { msSsim = 0.97f, deltaE2000 = 3.0f, alphaIoU = 0.98f, alphaRMSE = 6f / 255f, normalAngleDegP95 = 4f, grayRMSE = 3f / 255f };
                case ATOQualityPreset.Low:
                    return new ATOMetricThresholds { msSsim = 0.93f, deltaE2000 = 6.0f, alphaIoU = 0.95f, alphaRMSE = 12f / 255f, normalAngleDegP95 = 8f, grayRMSE = 6f / 255f };
                case ATOQualityPreset.Custom:
                    // 自定义挡位无内置值：默认全 1 近无损，由用户修改且不被其他挡位覆盖。
                    // The Custom preset has no built-in values: near-lossless by default, user-editable and never overridden by other presets.
                    return Lossless();
                case ATOQualityPreset.High:
                default:
                    return new ATOMetricThresholds { msSsim = 0.99f, deltaE2000 = 1.0f, alphaIoU = 0.995f, alphaRMSE = 2f / 255f, normalAngleDegP95 = 1.5f, grayRMSE = 1f / 255f };
            }
        }

        public bool IsLossless
        {
            get
            {
                return msSsim >= 1f && deltaE2000 <= 0f && alphaIoU >= 1f && alphaRMSE <= 0f
                       && normalAngleDegP95 <= 0f && grayRMSE <= 0f;
            }
        }

        public ATOMetricThresholds Clone()
        {
            return (ATOMetricThresholds)MemberwiseClone();
        }
    }

    // 平台覆盖：勾选后覆盖对应平台的所有优化参数。Platform override: when enabled, overrides all optimization parameters for that platform.
    [Serializable]
    public class ATOPlatformOverride
    {
        public ATOPlatform platform = ATOPlatform.PC;
        public bool enabled = false;
        public ATOQualityPreset qualityPreset = ATOQualityPreset.High;
        public ATOMetricThresholds customMetrics = ATOMetricThresholds.Lossless();
        public ATOFormatSettings formats = new ATOFormatSettings();
        public bool npotAtlases = false;
        // 图集最大边长（移动端上限 4096）。Max atlas side (mobile capped at 4096).
        public int maxAtlasSize = ATOConstants.MaxAtlasSizeDesktop;

        public ATOPlatformOverride Clone()
        {
            return new ATOPlatformOverride
            {
                platform = platform,
                enabled = enabled,
                qualityPreset = qualityPreset,
                customMetrics = customMetrics.Clone(),
                formats = formats.Clone(),
                npotAtlases = npotAtlases,
                maxAtlasSize = maxAtlasSize
            };
        }
    }

    // 全部优化设置（序列化在组件上）。All optimization settings (serialized on the component).
    [Serializable]
    public class ATOSettings
    {
        // 是否生成图集（默认勾选）。不勾选则：不生成图集、不剔除未使用 UV、不重排 UV，直接缩放整张贴图并做其他优化。
        // Whether to generate atlases (default on). When off: no atlases, no unused-UV trimming, no UV re-layout; whole textures are scaled instead.
        public bool generateAtlas = true;

        public ATOQualityPreset qualityPreset = ATOQualityPreset.High;

        // 自定义挡位参数（仅 qualityPreset == Custom 时生效）。Custom preset metrics (only used when qualityPreset == Custom).
        public ATOMetricThresholds customMetrics = ATOMetricThresholds.Lossless();

        // 最小像素密度（px/m）。Minimum pixel density in px per meter.
        public float minDensityPxPerMeter = ATOConstants.DefaultMinDensityPxPerMeter;

        // 最大像素密度（px/m）。Maximum pixel density in px per meter.
        public float maxDensityPxPerMeter = ATOConstants.DefaultMaxDensityPxPerMeter;

        // 图集最小 padding（px），可选 4/8/16/32/64；实际 padding = max(选项, ceil(图集最大边长/128))。
        // Minimum atlas padding in px (4/8/16/32/64); effective padding = max(option, ceil(atlas max side / 128)).
        public int atlasPaddingPx = ATOConstants.DefaultPaddingPx;

        // 实验性 NPOT 图集（默认关闭；开启时边长以 64 为步进）。Experimental NPOT atlases (off by default; side step 64 when on).
        public bool npotAtlases = false;

        // 图集最大边长。Maximum atlas side length.
        public int maxAtlasSize = ATOConstants.MaxAtlasSizeDesktop;

        public ATOFormatSettings formats = new ATOFormatSettings();

        // 贴图去重开关。Texture deduplication toggle.
        public bool deduplicateTextures = true;

        // 材质去重开关。Material deduplication toggle.
        public bool deduplicateMaterials = true;

        // 材质槽合并开关（含安全条件：动画不单独切换其中任一材质槽）。Material slot merge toggle (guarded by animation-safety conditions).
        public bool mergeMaterialSlots = true;

        // 详细日志（默认开启，便于开发调试；可关闭）。Verbose logging (on by default for debugging; can be turned off).
        public bool verboseLog = true;

        // i18n 语言："Auto" 读取 NDMF 当前语言配置；否则为语言代码（如 "en-us" / "zh-hans"）。
        // UI language: "Auto" follows NDMF's language config; otherwise a language code (e.g. "en-us" / "zh-hans").
        public string language = "Auto";

        // 各平台覆盖。Per-platform overrides.
        public List<ATOPlatformOverride> platformOverrides = new List<ATOPlatformOverride>();

        // 当前平台生效的质量阈值。Resolved quality thresholds for the given platform.
        public ATOMetricThresholds ResolveMetrics(ATOPlatform platform)
        {
            var ov = FindOverride(platform);
            if (ov != null && ov.enabled)
            {
                return ov.qualityPreset == ATOQualityPreset.Custom ? ov.customMetrics : ATOMetricThresholds.ForPreset(ov.qualityPreset);
            }
            return qualityPreset == ATOQualityPreset.Custom ? customMetrics : ATOMetricThresholds.ForPreset(qualityPreset);
        }

        // 当前平台生效的格式设置。Resolved format settings for the given platform.
        public ATOFormatSettings ResolveFormats(ATOPlatform platform)
        {
            var ov = FindOverride(platform);
            return ov != null && ov.enabled ? ov.formats : formats;
        }

        // 当前平台生效的 NPOT 开关。Resolved NPOT toggle for the given platform.
        public bool ResolveNpotAtlases(ATOPlatform platform)
        {
            var ov = FindOverride(platform);
            return ov != null && ov.enabled ? ov.npotAtlases : npotAtlases;
        }

        // 当前平台生效的图集最大边长（移动端硬钳制 4096）。Resolved max atlas side (mobile hard-capped at 4096).
        public int ResolveMaxAtlasSize(ATOPlatform platform)
        {
            var ov = FindOverride(platform);
            int v = ov != null && ov.enabled ? ov.maxAtlasSize : maxAtlasSize;
            if (platform != ATOPlatform.PC && v > ATOConstants.MaxAtlasSizeMobile) v = ATOConstants.MaxAtlasSizeMobile;
            if (v < ATOConstants.MinAtlasSize) v = ATOConstants.MinAtlasSize;
            return v;
        }

        public ATOPlatformOverride FindOverride(ATOPlatform platform)
        {
            if (platformOverrides == null) return null;
            foreach (var ov in platformOverrides)
            {
                if (ov != null && ov.platform == platform) return ov;
            }
            return null;
        }

        // 归一化并校验设置。Normalizes and validates settings.
        public void Normalize()
        {
            minDensityPxPerMeter = Mathf.Clamp(minDensityPxPerMeter, 1f, 65536f);
            maxDensityPxPerMeter = Mathf.Clamp(maxDensityPxPerMeter, minDensityPxPerMeter, 65536f);
            if (!IsValidPadding(atlasPaddingPx)) atlasPaddingPx = ATOConstants.DefaultPaddingPx;
            maxAtlasSize = Mathf.Clamp(maxAtlasSize, ATOConstants.MinAtlasSize, ATOConstants.MaxAtlasSizeDesktop);
            if (customMetrics == null) customMetrics = ATOMetricThresholds.Lossless();
            if (formats == null) formats = new ATOFormatSettings();
            if (platformOverrides == null) platformOverrides = new List<ATOPlatformOverride>();
            if (string.IsNullOrEmpty(language)) language = "Auto";
            for (int i = platformOverrides.Count - 1; i >= 0; i--)
            {
                if (platformOverrides[i] == null) { platformOverrides.RemoveAt(i); continue; }
                platformOverrides[i].maxAtlasSize = Mathf.Clamp(platformOverrides[i].maxAtlasSize, ATOConstants.MinAtlasSize, ATOConstants.MaxAtlasSizeDesktop);
                if (platformOverrides[i].customMetrics == null) platformOverrides[i].customMetrics = ATOMetricThresholds.Lossless();
                if (platformOverrides[i].formats == null) platformOverrides[i].formats = new ATOFormatSettings();
            }
        }

        public static bool IsValidPadding(int px)
        {
            foreach (var p in ATOConstants.PaddingOptions)
            {
                if (p == px) return true;
            }
            return false;
        }
    }
}
