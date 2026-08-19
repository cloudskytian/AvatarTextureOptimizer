// ============================================================================
// ATOSettings.cs — ATO 全部可序列化配置项 / All serializable settings for ATO
// (EN) This file defines the complete user-facing configuration model.
// (ZH) 本文件定义全部面向用户的配置模型。开发阶段可随意增删字段，无需考虑兼容。
// ============================================================================

using System;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    // -------------------------------------------------------------------------
    // 质量挡位 / Quality presets
    // 参考依据（学术/业界）：
    //  - SSIM / MS-SSIM: Wang et al. "Image Quality Assessment" (2003/2004)
    //    >= 0.98 通常视为"视觉无损"区间。
    //  - CIEDE2000 ΔE: Sharma et al. (2005)，ΔE<=1 不可察觉，1~2 需仔细对比，
    //    2~3.5 可接受。
    //  - 法线角度误差：<5° 通常不可察觉。
    //  - alpha：Cutout 用轮廓 IoU，Blend 用线性 RMSE。
    // -------------------------------------------------------------------------
    public enum ATOQualityPreset
    {
        /// <summary>(EN) User-defined, defaults near-lossless. (ZH) 自定义，默认近无损。</summary>
        Custom = 0,
        /// <summary>(EN) Near-lossless: quality target 1, skip island scaling. (ZH) 近无损：目标质量1，跳过缩放。</summary>
        Lossless = 1,
        /// <summary>(EN) Ultra: near-imperceptible. (ZH) 超高质量：几乎不可察觉。</summary>
        Ultra = 2,
        /// <summary>(EN) High (default): visually lossless in practice. (ZH) 高（默认）：实践视觉无损。</summary>
        High = 3,
        /// <summary>(EN) Balanced: visible only on close inspection. (ZH) 平衡：仅在仔细对比时可见。</summary>
        Balanced = 4,
        /// <summary>(EN) Aggressive: aggressive size reduction. (ZH) 激进：大幅压缩体积。</summary>
        Aggressive = 5,
    }

    /// <summary>(EN) Per-texture-kind quality thresholds. (ZH) 各贴图类型的质量阈值。</summary>
    [Serializable]
    public class ATOQualityThresholds
    {
        [Tooltip("(EN) MS-SSIM threshold (opaque & transparent color). (ZH) MS-SSIM 阈值（不透明/透明颜色）。")]
        [Range(0.5f, 1f)]
        public float msSsim = 0.985f;

        [Tooltip("(EN) CIEDE2000 ΔE threshold. (ZH) CIEDE2000 ΔE 阈值。")]
        [Range(0.1f, 10f)]
        public float deltaE2000 = 2.0f;

        [Tooltip("(EN) Alpha IoU threshold for Cutout. (ZH) Cutout 的 alpha 轮廓 IoU 阈值。")]
        [Range(0.5f, 1f)]
        public float alphaIoU = 0.99f;

        [Tooltip("(EN) Alpha linear RMSE threshold for Blend (0..1). (ZH) Blend 的 alpha 线性 RMSE 阈值 (0..1)。")]
        [Range(0f, 0.2f)]
        public float alphaRmse = 0.004f;

        [Tooltip("(EN) Normal map angle error threshold (degrees). (ZH) 法线贴图角度误差阈值（度）。")]
        [Range(0.1f, 30f)]
        public float normalAngleErrorDeg = 4.0f;

        [Tooltip("(EN) Normal map p95 percentile (0..1). (ZH) 法线贴图 p95 分位数 (0..1)。")]
        [Range(0.5f, 1f)]
        public float normalP95 = 0.95f;

        [Tooltip("(EN) Grayscale linear-space RMSE threshold (0..1). (ZH) 灰度贴图线性空间 RMSE 阈值 (0..1)。")]
        [Range(0f, 0.2f)]
        public float grayRmse = 0.004f;

        /// <summary>(EN) Copy all fields from another instance. (ZH) 从另一实例拷贝全部字段。</summary>
        public void CopyFrom(ATOQualityThresholds other)
        {
            msSsim = other.msSsim;
            deltaE2000 = other.deltaE2000;
            alphaIoU = other.alphaIoU;
            alphaRmse = other.alphaRmse;
            normalAngleErrorDeg = other.normalAngleErrorDeg;
            normalP95 = other.normalP95;
            grayRmse = other.grayRmse;
        }
    }

    /// <summary>(EN) Full quality configuration for one preset. (ZH) 单个挡位的完整质量配置。</summary>
    [Serializable]
    public class ATOQualitySettings
    {
        [Tooltip("(EN) Active quality preset. (ZH) 当前质量挡位。")]
        public ATOQualityPreset preset = ATOQualityPreset.High;

        [Header("Custom thresholds (自定义阈值)")]
        [Tooltip("(EN) Custom preset thresholds; defaults near-lossless (all ~1). (ZH) 自定义挡位阈值；默认近无损（全为1）。")]
        public ATOQualityThresholds custom = new ATOQualityThresholds();

        // 像素密度 / pixel density (px per meter)
        [Header("Pixel density (像素密度)")]
        [Tooltip("(EN) Min pixel density (px/m). (ZH) 最小像素密度（px/米）。")]
        public float minPixelDensity = 2048f;
        [Tooltip("(EN) Max pixel density (px/m). (ZH) 最大像素密度（px/米）。")]
        public float maxPixelDensity = 4096f;

        // 岛回退阈值 / island fallback thresholds
        [Header("Island fallback (岛回退)")]
        [Tooltip("(EN) Islands with bounding-box short side < this use single-scale SSIM. (ZH) 包围盒短边小于该值回退单尺度 SSIM。")]
        public int ssImSingleScaleShortSide = 176;
        [Tooltip("(EN) Islands with bounding-box short side < this skip SSIM/MS-SSIM entirely. (ZH) 包围盒短边小于该值直接忽略 SSIM/MS-SSIM。")]
        public int ignoreSsimShortSide = 11;

        /// <summary>(EN) Get effective thresholds for the active preset. (ZH) 取当前挡位的有效阈值。</summary>
        public ATOQualityThresholds GetEffective()
        {
            var t = new ATOQualityThresholds();
            switch (preset)
            {
                case ATOQualityPreset.Lossless:
                    t.msSsim = 0.999f; t.deltaE2000 = 0.5f; t.alphaIoU = 0.999f;
                    t.alphaRmse = 0.0005f; t.normalAngleErrorDeg = 1.0f; t.normalP95 = 0.99f; t.grayRmse = 0.0005f;
                    break;
                case ATOQualityPreset.Ultra:
                    t.msSsim = 0.995f; t.deltaE2000 = 1.0f; t.alphaIoU = 0.995f;
                    t.alphaRmse = 0.001f; t.normalAngleErrorDeg = 2.0f; t.normalP95 = 0.97f; t.grayRmse = 0.001f;
                    break;
                case ATOQualityPreset.High:
                    t.msSsim = 0.985f; t.deltaE2000 = 2.0f; t.alphaIoU = 0.99f;
                    t.alphaRmse = 0.004f; t.normalAngleErrorDeg = 4.0f; t.normalP95 = 0.95f; t.grayRmse = 0.004f;
                    break;
                case ATOQualityPreset.Balanced:
                    t.msSsim = 0.975f; t.deltaE2000 = 3.0f; t.alphaIoU = 0.98f;
                    t.alphaRmse = 0.008f; t.normalAngleErrorDeg = 6.0f; t.normalP95 = 0.95f; t.grayRmse = 0.008f;
                    break;
                case ATOQualityPreset.Aggressive:
                    t.msSsim = 0.95f; t.deltaE2000 = 4.0f; t.alphaIoU = 0.96f;
                    t.alphaRmse = 0.012f; t.normalAngleErrorDeg = 8.0f; t.normalP95 = 0.95f; t.grayRmse = 0.012f;
                    break;
                case ATOQualityPreset.Custom:
                default:
                    t.CopyFrom(custom);
                    break;
            }
            return t;
        }
    }

    /// <summary>(EN) Supported target platforms. (ZH) 支持的目标平台。</summary>
    public enum ATOBuildPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2,
    }

    /// <summary>(EN) Safe compression format enumeration. (ZH) 安全压缩格式枚举。</summary>
    public enum ATOCompressionFormat
    {
        Auto = 0,
        Uncompressed = 1,
        ASTC = 2,
        BC7 = 3,
        BC5 = 4,
        BC3 = 5,
        BC1 = 6,
        ETC2_RGB = 7,
        ETC2_RGBA = 8,
        R8 = 9,
        RGB24 = 10,
        RGBA32 = 11,
    }

    /// <summary>(EN) Texture classification used for compression & MipStreaming toggles. (ZH) 用于压缩与 MipStreaming 开关的贴图分类。</summary>
    public enum ATOTextureClass
    {
        Opaque = 0,
        Transparent = 1,
        Normal = 2,
        Grayscale = 3,
    }

    /// <summary>(EN) Atlas padding options. (ZH) 图集 padding 挡位。</summary>
    public enum ATOPadding
    {
        Px4 = 4,
        Px8 = 8,
        Px16 = 16,
        Px32 = 32,
        Px64 = 64,
    }

    /// <summary>(EN) i18n language selection. (ZH) i18n 语言选择。</summary>
    public enum ATOLanguage
    {
        Auto = 0,
        English = 1,
        SimplifiedChinese = 2,
    }

    /// <summary>(EN) Per-texture-class import parameters. (ZH) 各贴图分类的导入参数。</summary>
    [Serializable]
    public class ATOTextureImportSettings
    {
        [Tooltip("(EN) Compression format for this texture class. (ZH) 该贴图分类的压缩格式。")]
        public ATOCompressionFormat format = ATOCompressionFormat.Auto;

        [Tooltip("(EN) Enable mipmaps (bound to MipStreaming per VRChat rule). (ZH) 开启 mipmap（与 MipStreaming 绑定，VRChat 要求）。")]
        public bool mipmaps = true;
    }

    /// <summary>(EN) Compression/import settings for all texture classes. (ZH) 全部贴图分类的压缩/导入设置。</summary>
    [Serializable]
    public class ATOCompressionSettings
    {
        public ATOTextureImportSettings opaque = new ATOTextureImportSettings();
        public ATOTextureImportSettings transparent = new ATOTextureImportSettings();
        public ATOTextureImportSettings normal = new ATOTextureImportSettings();
        public ATOTextureImportSettings grayscale = new ATOTextureImportSettings();

        public ATOTextureImportSettings Get(ATOTextureClass c)
        {
            switch (c)
            {
                case ATOTextureClass.Transparent: return transparent;
                case ATOTextureClass.Normal: return normal;
                case ATOTextureClass.Grayscale: return grayscale;
                default: return opaque;
            }
        }
    }

    /// <summary>(EN) Atlas generation options. (ZH) 图集生成选项。</summary>
    [Serializable]
    public class ATOAtlasSettings
    {
        [Tooltip("(EN) Generate atlases (cull unused UV, repack). (ZH) 生成图集（剔除未用UV、重排UV）。")]
        public bool enableAtlas = true;

        [Tooltip("(EN) Island padding. (ZH) 岛间 padding。")]
        public ATOPadding padding = ATOPadding.Px4;

        [Tooltip("(EN) Allow NPOT atlas sizes. (ZH) 允许 NPOT 图集边长。")]
        public bool allowNPot = false;

        [Tooltip("(EN) Max atlas edge (PC). (ZH) 最大图集边长（PC）。")]
        public int maxAtlasSizePC = 8192;

        [Tooltip("(EN) Max atlas edge (mobile). (ZH) 最大图集边长（移动端）。")]
        public int maxAtlasSizeMobile = 4096;
    }

    /// <summary>(EN) Per-platform override of optimization parameters. (ZH) 各平台的参数 override。 </summary>
    [Serializable]
    public class ATOPlatformOverride
    {
        [Tooltip("(EN) Enable per-platform override for this platform. (ZH) 为该平台启用 override。")]
        public bool enabled = false;
        public ATOBuildPlatform platform = ATOBuildPlatform.PC;
        public ATOCompressionSettings compression = new ATOCompressionSettings();
        public ATOAtlasSettings atlas = new ATOAtlasSettings();
    }

    /// <summary>(EN) Deduplication toggles. (ZH) 去重开关。</summary>
    [Serializable]
    public class ATODedupSettings
    {
        [Tooltip("(EN) Deduplicate identical materials after optimization. (ZH) 优化后合并内容与参数完全相同的材质。")]
        public bool materials = true;
        [Tooltip("(EN) Deduplicate identical textures/atlases after optimization. (ZH) 优化后合并完全相同的贴图/图集。")]
        public bool textures = true;
    }
}
