using System;
using System.Collections.Generic;
using UnityEngine;

// Serializable settings data model for ATO.
// ATO 的可序列化设置数据模型。

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Metric thresholds of one quality tier.
    /// 一个质量挡位的指标阈值。
    /// </summary>
    [Serializable]
    public sealed class QualityTierSettings
    {
        /// <summary>Overall target quality 0..1. 1.0 = near-lossless (copy without resampling). 总目标质量 0..1。1.0=近无损（不重采样原样拷贝）。</summary>
        [Range(0f, 1f)] public float targetQuality = 0.95f;
        /// <summary>Minimum MS-SSIM / SSIM. MS-SSIM/SSIM 下限。</summary>
        [Range(0f, 1f)] public float minSSIM = 0.98f;
        /// <summary>Maximum mean CIEDE2000 color difference. CIEDE2000 平均色差上限。</summary>
        [Range(0f, 20f)] public float maxDeltaE = 1.0f;
        /// <summary>Maximum linear alpha RMSE for Blend mode. Blend 模式线性 alpha RMSE 上限。</summary>
        [Range(0f, 1f)] public float maxAlphaRMSE = 0.005f;
        /// <summary>Minimum cutout coverage IoU. Cutout 覆盖率 IoU 下限。</summary>
        [Range(0f, 1f)] public float minCutoutIoU = 0.999f;
        /// <summary>Maximum p95 normal-map angle error in degrees. 法线贴图 p95 角度误差上限（度）。</summary>
        [Range(0f, 20f)] public float maxNormalAngleDeg = 0.5f;
        /// <summary>Maximum per-channel linear RMSE for grayscale masks. 灰度蒙版逐通道线性 RMSE 上限。</summary>
        [Range(0f, 1f)] public float maxGrayRMSE = 0.005f;

        public QualityTierSettings Clone() => (QualityTierSettings)MemberwiseClone();
    }

    /// <summary>
    /// Per-platform override of optimization parameters (mirrors Unity's platform override concept).
    /// 平台参数覆盖（参考 Unity platform override 概念）。
    /// </summary>
    [Serializable]
    public sealed class PlatformOverrideData
    {
        public ATOPlatform platform = ATOPlatform.PC;
        public bool enabled;
        public QualityTierId qualityTier = QualityTierId.High;
        public bool overrideCustomTier;            // when enabled, custom tier below is used. 启用时使用下面的自定义挡位。
        public QualityTierSettings customTier = DefaultTier(QualityTierId.Custom);
        [Range(512, 8192)] public int densityMinPxPerMeter = 2048;
        [Range(512, 8192)] public int densityMaxPxPerMeter = 4096;
        public bool generateAtlas = true;
        public AtlasSizeMode atlasSizeMode = AtlasSizeMode.PowerOfTwo;
        [Range(4, 64)] public int minPadding = 4;
        public MipMode mipColor = MipMode.On;
        public MipMode mipNormal = MipMode.On;
        public MipMode mipMask = MipMode.On;
        public ATOCompressionFormat compressionColorOpaque = ATOCompressionFormat.Automatic;
        public ATOCompressionFormat compressionColorAlpha = ATOCompressionFormat.Automatic;
        public ATOCompressionFormat compressionNormal = ATOCompressionFormat.Automatic;
        public ATOCompressionFormat compressionMask = ATOCompressionFormat.Automatic;

        public static QualityTierSettings DefaultTier(QualityTierId id)
        {
            // Research-based defaults (see DESIGN.md "Quality tiers"): SSIM thresholds from Wang et al.,
            // CIEDE2000 JND ~1.0-2.3 (ISO/CIE perception), alpha IoU/RMSE and normal angle pragmatics.
            // 基于研究参考的默认值（见 DESIGN.md「质量挡位」）：SSIM 参考 Wang 等，CIEDE2000 JND≈1.0-2.3，alpha 与法线角度为工程经验值。
            var t = new QualityTierSettings();
            switch (id)
            {
                case QualityTierId.Ultra:
                    t.targetQuality = 1.0f; t.minSSIM = 0.999f; t.maxDeltaE = 0.5f; t.maxAlphaRMSE = 0.002f; t.minCutoutIoU = 0.9998f; t.maxNormalAngleDeg = 0.25f; t.maxGrayRMSE = 0.002f; break;
                case QualityTierId.High:
                    t.targetQuality = 0.95f; t.minSSIM = 0.98f; t.maxDeltaE = 1.0f; t.maxAlphaRMSE = 0.005f; t.minCutoutIoU = 0.999f; t.maxNormalAngleDeg = 0.5f; t.maxGrayRMSE = 0.005f; break;
                case QualityTierId.Medium:
                    t.targetQuality = 0.9f; t.minSSIM = 0.96f; t.maxDeltaE = 2.3f; t.maxAlphaRMSE = 0.012f; t.minCutoutIoU = 0.996f; t.maxNormalAngleDeg = 1.0f; t.maxGrayRMSE = 0.012f; break;
                case QualityTierId.Low:
                    t.targetQuality = 0.85f; t.minSSIM = 0.94f; t.maxDeltaE = 3.5f; t.maxAlphaRMSE = 0.02f; t.minCutoutIoU = 0.99f; t.maxNormalAngleDeg = 2.0f; t.maxGrayRMSE = 0.02f; break;
                case QualityTierId.Minimum:
                    t.targetQuality = 0.8f; t.minSSIM = 0.91f; t.maxDeltaE = 5.0f; t.maxAlphaRMSE = 0.032f; t.minCutoutIoU = 0.98f; t.maxNormalAngleDeg = 3.0f; t.maxGrayRMSE = 0.032f; break;
                case QualityTierId.Custom:
                default:
                    // All thresholds = 1 (near-lossless) until the user edits them. 全部=1（近无损），由用户编辑。
                    t.targetQuality = 1.0f; t.minSSIM = 0.999f; t.maxDeltaE = 1.0f; t.maxAlphaRMSE = 0.01f; t.minCutoutIoU = 0.999f; t.maxNormalAngleDeg = 1.0f; t.maxGrayRMSE = 0.01f; break;
            }
            return t;
        }
    }

    /// <summary>
    /// The serialized settings of an ATOSettings component.
    /// ATOSettings 组件的序列化设置。
    /// </summary>
    [Serializable]
    public sealed class ATOSettingsData
    {
        // ---- Base (all-platform) settings. 全平台基准设置。----
        public QualityTierId qualityTier = QualityTierId.High;
        public QualityTierSettings customTier = PlatformOverrideData.DefaultTier(QualityTierId.Custom);
        [Range(512, 8192)] public int densityMinPxPerMeter = 2048;
        [Range(512, 8192)] public int densityMaxPxPerMeter = 4096;
        public bool generateAtlas = true;
        public AtlasSizeMode atlasSizeMode = AtlasSizeMode.PowerOfTwo;
        [Range(4, 64)] public int minPadding = 4;
        public MipMode mipColor = MipMode.On;
        public MipMode mipNormal = MipMode.On;
        public MipMode mipMask = MipMode.On;
        public ATOCompressionFormat compressionColorOpaque = ATOCompressionFormat.Automatic;
        public ATOCompressionFormat compressionColorAlpha = ATOCompressionFormat.Automatic;
        public ATOCompressionFormat compressionNormal = ATOCompressionFormat.Automatic;
        public ATOCompressionFormat compressionMask = ATOCompressionFormat.Automatic;

        // ---- Whitelist (meshes / materials / textures / animation clips / renderers...). 白名单（对象类型不限）。----
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        // ---- i18n. 本地化。----
        public ATOLanguageMode languageMode = ATOLanguageMode.Auto;
        public string manualLanguage = "en-US";

        // ---- Per-platform overrides. 平台覆盖。----
        public List<PlatformOverrideData> platformOverrides = new List<PlatformOverrideData>
        {
            new PlatformOverrideData { platform = ATOPlatform.PC },
            new PlatformOverrideData { platform = ATOPlatform.Android },
            new PlatformOverrideData { platform = ATOPlatform.iOS },
        };

        /// <summary>
        /// Merges the base settings with the (enabled) override for the given platform.
        /// 将基准设置与给定平台（已启用）的覆盖合并。
        /// </summary>
        public ATOSettingsData Resolve(ATOPlatform platform)
        {
            var ov = platformOverrides.Find(o => o.platform == platform && o.enabled);
            if (ov == null) return this;

            var d = new ATOSettingsData();
            // Copy base then apply override fields. 复制基准，再应用覆盖字段。
            d.qualityTier = ov.qualityTier;
            d.customTier = ov.overrideCustomTier ? ov.customTier.Clone() : customTier.Clone();
            d.densityMinPxPerMeter = ov.densityMinPxPerMeter;
            d.densityMaxPxPerMeter = ov.densityMaxPxPerMeter;
            d.generateAtlas = ov.generateAtlas;
            d.atlasSizeMode = ov.atlasSizeMode;
            d.minPadding = ov.minPadding;
            d.mipColor = ov.mipColor;
            d.mipNormal = ov.mipNormal;
            d.mipMask = ov.mipMask;
            d.compressionColorOpaque = ov.compressionColorOpaque;
            d.compressionColorAlpha = ov.compressionColorAlpha;
            d.compressionNormal = ov.compressionNormal;
            d.compressionMask = ov.compressionMask;
            d.whitelist = new List<UnityEngine.Object>(whitelist);
            d.languageMode = languageMode;
            d.manualLanguage = manualLanguage;
            d.platformOverrides = platformOverrides;
            return d;
        }

        /// <summary>
        /// Returns the effective quality thresholds for the selected tier (custom tier uses user values).
        /// 返回所选挡位的有效质量阈值（自定义挡位用用户值）。
        /// </summary>
        public QualityTierSettings GetTier() =>
            qualityTier == QualityTierId.Custom ? customTier.Clone() : PlatformOverrideData.DefaultTier(qualityTier);

        public ATOCompressionFormat CompressionFor(TextureClass cls)
        {
            switch (cls)
            {
                case TextureClass.ColorOpaque: return compressionColorOpaque;
                case TextureClass.ColorAlpha: return compressionColorAlpha;
                case TextureClass.Normal: return compressionNormal;
                default: return compressionMask;
            }
        }

        public MipMode MipFor(TextureClass cls)
        {
            switch (cls)
            {
                case TextureClass.ColorOpaque:
                case TextureClass.ColorAlpha: return mipColor;
                case TextureClass.Normal: return mipNormal;
                default: return mipMask;
            }
        }
    }
}
