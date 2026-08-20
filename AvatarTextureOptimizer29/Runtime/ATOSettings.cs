// ATO settings model (runtime-serializable, lives on the component).
// ATO 配置模型（运行时序列化，挂在组件上）。
// NOTE: This is under active development; fields may change freely (no migration).
// 注意：开发阶段，字段可任意变更，不做版本兼容。

using System;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>Quality preset gear. / 质量挡位。</summary>
    public enum AtoQualityPreset
    {
        NearLossless = 0, // 近无损（Custom 档默认值即此档）
        High = 1,
        Balanced = 2,     // default / 默认
        Fast = 3,
        Custom = 4,       // user params, never overwritten by other presets / 用户参数，不被其他挡位覆盖
    }

    /// <summary>Individual quality thresholds. Values are per docs/QualityPresets.md.
    /// 独立质量阈值，数值依据 docs/QualityPresets.md。</summary>
    [Serializable]
    public class AtoQualityParams
    {
        [Range(0.90f, 1f)] public float msssimMin = 0.98f;          // MS-SSIM (luma) lower bound
        [Range(0f, 5f)] public float deltaEMeanMax = 1.5f;          // CIEDE2000 mean upper bound
        [Range(0f, 8f)] public float deltaEP95Max = 3.5f;           // CIEDE2000 p95 upper bound
        [Range(0f, 8f)] public float normalAngleMeanMax = 1.5f;     // deg, decoded normal angle error mean
        [Range(0f, 16f)] public float normalAngleP95Max = 4f;       // deg, p95
        [Range(0.90f, 1f)] public float alphaCutoutIoUMin = 0.995f; // silhouette IoU after cutoff clip
        [Range(0f, 0.1f)] public float alphaBlendRmseMax = 3f / 255f; // linear alpha RMSE
        [Range(0f, 0.1f)] public float grayRmseMax = 2.5f / 255f;   // per-used-channel linear RMSE (worst)

        public AtoQualityParams Clone() => (AtoQualityParams)MemberwiseClone();

        public static bool NearEquals(AtoQualityParams a, AtoQualityParams b) =>
            Mathf.Abs(a.msssimMin - b.msssimMin) < 1e-4f
            && Mathf.Abs(a.deltaEMeanMax - b.deltaEMeanMax) < 1e-3f
            && Mathf.Abs(a.deltaEP95Max - b.deltaEP95Max) < 1e-3f
            && Mathf.Abs(a.normalAngleMeanMax - b.normalAngleMeanMax) < 1e-3f
            && Mathf.Abs(a.normalAngleP95Max - b.normalAngleP95Max) < 1e-3f
            && Mathf.Abs(a.alphaCutoutIoUMin - b.alphaCutoutIoUMin) < 1e-4f
            && Mathf.Abs(a.alphaBlendRmseMax - b.alphaBlendRmseMax) < 1e-4f
            && Mathf.Abs(a.grayRmseMax - b.grayRmseMax) < 1e-4f;
    }

    /// <summary>Safe compression format choices (filtered per platform/category at runtime).
    /// 安全压缩格式枚举（UI 会按平台/类别/alpha 过滤）。</summary>
    public enum AtoTexFormat
    {
        Auto = 0,
        // PC
        DXT1 = 1, DXT5 = 2, BC7 = 3, DXT1Crunched = 4, DXT5Crunched = 5,
        // Mobile (Android / iOS)
        ASTC_4x4 = 10, ASTC_5x5 = 11, ASTC_6x6 = 12, ASTC_8x8 = 13,
    }

    /// <summary>Per texture-category import params. / 按贴图类别的导入参数。</summary>
    [Serializable]
    public class AtoCategoryParams
    {
        // Single switch controls both Mipmap & MipStreaming (VRChat: mips on -> streaming must be on).
        // 单开关同时控制 Mipmap 与 MipStreaming（VRChat 要求开 Mipmap 必开 MipStreaming）。
        public bool mipsAndStreaming = true;
        public AtoTexFormat format = AtoTexFormat.Auto;
    }

    /// <summary>Target platform for overrides. / 平台覆写目标。</summary>
    public enum AtoPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2,
    }

    /// <summary>Per-platform optimization parameters. / 平台级优化参数。</summary>
    [Serializable]
    public class AtoPlatformSettings
    {
        public bool useOverride = false; // if false, effective == PC defaults (common best) / 未勾选时用通用最优解

        public AtoQualityPreset preset = AtoQualityPreset.Balanced;
        // Custom preset params; defaults = near lossless ("all 1"). / Custom 档参数，默认近无损。
        public AtoQualityParams custom = new AtoQualityParams();

        [Range(64, 16384)] public int minDensity = 2048; // px per meter / 像素密度下限
        [Range(64, 16384)] public int maxDensity = 4096; // px per meter / 像素密度上限

        public bool experimentalNpot = false; // candidate pool in 64px steps / 实验性 NPOT 分辨率

        [Min(4)] public int minPadding = 4;   // island gap floor; options 4/8/16/32/64 / 最小 padding

        public AtoCategoryParams opaque = new AtoCategoryParams();
        public AtoCategoryParams alpha = new AtoCategoryParams();
        public AtoCategoryParams normal = new AtoCategoryParams();
        public AtoCategoryParams gray = new AtoCategoryParams();

        public AtoCategoryParams GetCategory(AtoTexCategory cat)
        {
            switch (cat)
            {
                case AtoTexCategory.Alpha: return alpha;
                case AtoTexCategory.Normal: return normal;
                case AtoTexCategory.Gray: return gray;
                default: return opaque;
            }
        }
    }

    /// <summary>Texture categories used for compression / mip settings. / 贴图类别。</summary>
    public enum AtoTexCategory
    {
        Opaque = 0, // opaque color / 不透明主色
        Alpha = 1,  // color with alpha / 透明主色
        Normal = 2, // normal maps / 法线
        Gray = 3,   // masks & grayscale / 蒙版与灰度
    }

    /// <summary>Log verbosity. / 日志级别。</summary>
    public enum AtoLogLevel
    {
        Silent = 0,
        Info = 1,
        Debug = 2,
        Trace = 3,
    }
}
