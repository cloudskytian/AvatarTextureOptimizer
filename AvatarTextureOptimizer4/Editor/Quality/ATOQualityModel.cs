// Avatar Texture Optimizer (ATO)
// Quality level presets and threshold resolution.
// 质量挡位预设与阈值解析。
//
// Preset rationale (informed by academic/industry references):
//   - SSIM/MS-SSIM >= 0.95 is widely cited as "high quality"; >= 0.99 near-lossless.
//   - CIEDE2000: dE <= 1 imperceptible, <= 2.3 JND (just noticeable), <= 4-6 acceptable for game textures.
//   - Normal-map angular error: <= 3° visually negligible; <= 10° acceptable.
// 预设依据（参考学术/业内结论）：SSIM/MS-SSIM≥0.95 常被视为"高质量"、≥0.99 近无损；
// CIEDE2000：ΔE≤1 不可感知、≤2.3 为 JND、≤4-6 为游戏贴图可接受；法线角度误差 ≤3° 视觉可忽略、≤10° 可接受。

using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Resolves a quality level into concrete metric thresholds.
    /// 把质量挡位解析为具体指标阈值。
    /// </summary>
    public static class ATOQualityModel
    {
        public static ATOQualityThresholds Resolve(ATOBuildContext build)
        {
            var level = build.profile.qualityLevel;
            if (level == ATOQualityLevel.Custom)
                return build.profile.customThresholds; // Custom thresholds from the resolved profile. / Custom 阈值取自已解析的 profile。

            switch (level)
            {
                case ATOQualityLevel.Ultra:
                    return new ATOQualityThresholds
                    {
                        targetQuality = 1f, msSsimMin = 1f, deltaEMax = 0.5f,
                        alphaRmseMax = 0.005f, alphaIoUMin = 0.999f, angleDegMax = 0.5f, grayRmseMax = 0.005f
                    };
                case ATOQualityLevel.High:
                    return new ATOQualityThresholds
                    {
                        targetQuality = 0.98f, msSsimMin = 0.985f, deltaEMax = 2.5f,
                        alphaRmseMax = 0.02f, alphaIoUMin = 0.985f, angleDegMax = 3f, grayRmseMax = 0.02f
                    };
                case ATOQualityLevel.Medium:
                    return new ATOQualityThresholds
                    {
                        targetQuality = 0.90f, msSsimMin = 0.95f, deltaEMax = 4f,
                        alphaRmseMax = 0.04f, alphaIoUMin = 0.96f, angleDegMax = 6f, grayRmseMax = 0.04f
                    };
                case ATOQualityLevel.Low:
                    return new ATOQualityThresholds
                    {
                        targetQuality = 0.80f, msSsimMin = 0.90f, deltaEMax = 6f,
                        alphaRmseMax = 0.08f, alphaIoUMin = 0.92f, angleDegMax = 10f, grayRmseMax = 0.08f
                    };
                default:
                    return new ATOQualityThresholds
                    {
                        targetQuality = 0.98f, msSsimMin = 0.985f, deltaEMax = 2.5f,
                        alphaRmseMax = 0.02f, alphaIoUMin = 0.985f, angleDegMax = 3f, grayRmseMax = 0.02f
                    };
            }
        }

        /// <summary>True when the target quality is ~lossless (skip island scaling). / 目标质量≈无损时返回真（跳过岛缩放）。</summary>
        public static bool IsLossless(ATOQualityThresholds t) => t.targetQuality >= 0.9999f;
    }
}
