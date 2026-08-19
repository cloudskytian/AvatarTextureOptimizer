using System;

namespace NetFosa.AvatarTextureOptimizer
{
    /// <summary>
    /// 一组质量阈值。全部指标达标才判定"通过"。
    /// A set of quality thresholds. A scale candidate passes only when ALL metrics pass.
    /// </summary>
    [Serializable]
    public class QualityThresholds
    {
        /// <summary>目标质量 0..1（1 = 近无损 → 跳过缩放）。</summary>
        public float quality = 1.0f;

        /// <summary>MS-SSIM 最小值（0..1），用于原尺寸包围盒短边 ≥176px 的岛。</summary>
        public float msSsim = 0.98f;

        /// <summary>单尺度 SSIM 最小值，用于包围盒短边 &lt;176px 的岛（短边 &lt;11px 时忽略）。</summary>
        public float ssim = 0.985f;

        /// <summary>CIEDE2000 平均 ΔE 最大值。</summary>
        public float deltaE2000 = 3.0f;

        /// <summary>Cutout 模式：clip 后轮廓 IoU 最小值。</summary>
        public float alphaCutoutIoU = 0.98f;

        /// <summary>Blend 模式：alpha 线性 RMSE 最大值。</summary>
        public float alphaBlendRmse = 0.015f;

        /// <summary>法线贴图：角度误差 p95（度）最大值。</summary>
        public float normalAngleP95 = 3.0f;

        /// <summary>灰度贴图：线性空间逐通道 RMSE 最大值（取最差通道）。</summary>
        public float grayRmse = 0.02f;

        public QualityThresholds Clone() => (QualityThresholds)MemberwiseClone();

        /// <summary>该挡位是否等于"近无损"（quality == 1 → 跳过缩放、原样拷贝）。</summary>
        public bool IsNearLossless => quality >= 1.0f - 1e-6f;

        /// <summary>复制各字段（用于 Custom 挡位持久化编辑）。</summary>
        public void CopyFrom(QualityThresholds other)
        {
            quality = other.quality;
            msSsim = other.msSsim;
            ssim = other.ssim;
            deltaE2000 = other.deltaE2000;
            alphaCutoutIoU = other.alphaCutoutIoU;
            alphaBlendRmse = other.alphaBlendRmse;
            normalAngleP95 = other.normalAngleP95;
            grayRmse = other.grayRmse;
        }

        // ---- 各挡位出厂值（依据学术/业内感知研究，见 CLAUDE.md 第五节） ----
        public static QualityThresholds NearLossless() => new QualityThresholds
        {
            quality = 1.0f, msSsim = 1.0f, ssim = 1.0f, deltaE2000 = 0f,
            alphaCutoutIoU = 1.0f, alphaBlendRmse = 0f, normalAngleP95 = 0f, grayRmse = 0f,
        };

        public static QualityThresholds High() => new QualityThresholds
        {
            quality = 0.98f, msSsim = 0.995f, ssim = 0.998f, deltaE2000 = 1.5f,
            alphaCutoutIoU = 0.995f, alphaBlendRmse = 0.005f, normalAngleP95 = 1.5f, grayRmse = 0.010f,
        };

        public static QualityThresholds Balanced() => new QualityThresholds
        {
            quality = 0.95f, msSsim = 0.98f, ssim = 0.985f, deltaE2000 = 3.0f,
            alphaCutoutIoU = 0.98f, alphaBlendRmse = 0.015f, normalAngleP95 = 3.0f, grayRmse = 0.020f,
        };

        public static QualityThresholds Performance() => new QualityThresholds
        {
            quality = 0.90f, msSsim = 0.95f, ssim = 0.96f, deltaE2000 = 6.0f,
            alphaCutoutIoU = 0.95f, alphaBlendRmse = 0.040f, normalAngleP95 = 6.0f, grayRmse = 0.040f,
        };

        public static QualityThresholds Extreme() => new QualityThresholds
        {
            quality = 0.85f, msSsim = 0.90f, ssim = 0.92f, deltaE2000 = 10.0f,
            alphaCutoutIoU = 0.90f, alphaBlendRmse = 0.080f, normalAngleP95 = 10.0f, grayRmse = 0.080f,
        };

        public static QualityThresholds ForPreset(ATOQualityPreset preset)
        {
            switch (preset)
            {
                case ATOQualityPreset.NearLossless: return NearLossless();
                case ATOQualityPreset.High: return High();
                case ATOQualityPreset.Performance: return Performance();
                case ATOQualityPreset.Extreme: return Extreme();
                case ATOQualityPreset.Custom:
                case ATOQualityPreset.Balanced:
                default:
                    return Balanced();
            }
        }
    }

    /// <summary>
    /// 每类别压缩设置（图集 / fallback 贴图）。分类：不透明主色、透明主色（按图集是否含 alpha）、法线、灰度。
    /// Per-category compression settings.
    /// </summary>
    [Serializable]
    public class CompressionSettings
    {
        public ATOCompressionFormat mainOpaque = ATOCompressionFormat.Auto;
        public ATOCompressionFormat mainTransparent = ATOCompressionFormat.Auto;
        public ATOCompressionFormat normal = ATOCompressionFormat.Auto;
        public ATOCompressionFormat grayMask = ATOCompressionFormat.Auto;
        public ATOCompressionFormat other = ATOCompressionFormat.Auto;

        public ATOCompressionFormat Get(ATOTextureCategory category)
        {
            switch (category)
            {
                case ATOTextureCategory.MainOpaque: return mainOpaque;
                case ATOTextureCategory.MainTransparent: return mainTransparent;
                case ATOTextureCategory.Normal: return normal;
                case ATOTextureCategory.GrayMask: return grayMask;
                default: return other;
            }
        }

        public void Set(ATOTextureCategory category, ATOCompressionFormat format)
        {
            switch (category)
            {
                case ATOTextureCategory.MainOpaque: mainOpaque = format; break;
                case ATOTextureCategory.MainTransparent: mainTransparent = format; break;
                case ATOTextureCategory.Normal: normal = format; break;
                case ATOTextureCategory.GrayMask: grayMask = format; break;
                default: other = format; break;
            }
        }

        public CompressionSettings Clone()
        {
            return (CompressionSettings)MemberwiseClone();
        }
    }

    /// <summary>
    /// Mipmap / MipStreaming 设置。VRChat 要求开启 Mipmap 时强制开启 MipStreaming，
    /// 因此这里只提供一个开关，同时控制二者。
    /// </summary>
    [Serializable]
    public class MipmapSettings
    {
        /// <summary>主色。</summary>
        public bool main = true;
        /// <summary>法线。</summary>
        public bool normal = true;
        /// <summary>灰度/蒙版。</summary>
        public bool grayMask = true;
        /// <summary>其他。</summary>
        public bool other = true;

        public bool Get(ATOTextureCategory category)
        {
            switch (category)
            {
                case ATOTextureCategory.MainOpaque:
                case ATOTextureCategory.MainTransparent: return main;
                case ATOTextureCategory.Normal: return normal;
                case ATOTextureCategory.GrayMask: return grayMask;
                default: return other;
            }
        }

        public MipmapSettings Clone() => (MipmapSettings)MemberwiseClone();
    }

    /// <summary>
    /// 单个平台的全部优化参数覆盖。勾选 enabled 后覆盖全局值。
    /// Per-platform override of all optimization parameters.
    /// </summary>
    [Serializable]
    public class PlatformOverride
    {
        public bool enabled = false;

        public bool overrideQuality = false;
        public ATOQualityPreset qualityPreset = ATOQualityPreset.Balanced;
        public QualityThresholds customQuality = null;

        public bool overrideDensity = false;
        public int minPixelsPerMeter = 2048;
        public int maxPixelsPerMeter = 4096;

        public bool overrideCompression = false;
        public CompressionSettings compression = new CompressionSettings();

        public bool overrideMipmaps = false;
        public MipmapSettings mipmaps = new MipmapSettings();

        public bool overrideAtlas = false;
        public bool npotEnabled = false;
        public int minPadding = 4;
    }

    /// <summary>
    /// 平台覆盖集合（PC / Android / iOS）。
    /// </summary>
    [Serializable]
    public class PlatformOverrides
    {
        public PlatformOverride pc = new PlatformOverride();
        public PlatformOverride android = new PlatformOverride();
        public PlatformOverride ios = new PlatformOverride();

        public PlatformOverride Get(ATOPlatform platform)
        {
            switch (platform)
            {
                case ATOPlatform.Android: return android;
                case ATOPlatform.iOS: return ios;
                default: return pc;
            }
        }
    }
}
