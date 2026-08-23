// -----------------------------------------------------------------------------
// ATOPresets.cs — quality presets & research-based defaults.
// ATOPresets.cs — 质量挡位与基于研究的默认值。
//
// Basis / 依据:
//  - MS-SSIM ≥ 0.98 is commonly treated as "visually indistinguishable" for natural
//    imagery (Wang/Simoncelli/Bovik 2003; industry practice in perceptual codecs).
//    MS-SSIM ≥ 0.98 通常被视为自然图像"视觉不可区分"（Wang 等 2003 与感知编码业界实践）。
//  - CIEDE2000 JND ≈ 1.0 (Sharma et al. 2005): ΔE00 ≤ 1 is imperceptible for most
//    observers; 2–3 is noticeable on close inspection.
//    CIEDE2000 的 JND≈1.0（Sharma 等 2005）：ΔE00≤1 对多数观察者不可感知；2–3 需细看。
//  - Normal maps: angular error ≤1° mean / ≤3° p95 is below typical specular noise.
//    法线：平均≤1°、p95≤3° 低于常见高光噪声水平。
//  - "NearLossless" = MS-SSIM 1.0 → skip scaling entirely (raw copy), per spec.
//    NearLossless = MS-SSIM 1 → 完全跳过缩放（原样拷贝），按规格执行。
// -----------------------------------------------------------------------------

using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class ATOPresets
    {
        /// <summary>Apply preset values to a params object (never overwrites Custom).
        /// 将挡位值应用到参数对象（Custom 永不覆盖）。</summary>
        public static void Apply(ATOQualityPreset preset, ATOQualityParams q)
        {
            switch (preset)
            {
                case ATOQualityPreset.NearLossless:
                    q.msSsim = 1f;
                    q.deltaE = 0.5f;
                    q.alphaIou = 1f;
                    q.alphaRmse = 1f;
                    q.normalAngleMean = 0.5f;
                    q.normalAngleP95 = 1f;
                    q.grayRmse = 1f;
                    break;
                case ATOQualityPreset.High:
                    q.msSsim = 0.98f;
                    q.deltaE = 1.0f;
                    q.alphaIou = 0.995f;
                    q.alphaRmse = 2.5f;
                    q.normalAngleMean = 1.0f;
                    q.normalAngleP95 = 3.0f;
                    q.grayRmse = 2.0f;
                    break;
                case ATOQualityPreset.Medium:
                    q.msSsim = 0.95f;
                    q.deltaE = 2.0f;
                    q.alphaIou = 0.99f;
                    q.alphaRmse = 4f;
                    q.normalAngleMean = 1.5f;
                    q.normalAngleP95 = 5.0f;
                    q.grayRmse = 3.0f;
                    break;
                case ATOQualityPreset.Aggressive:
                    q.msSsim = 0.90f;
                    q.deltaE = 3.5f;
                    q.alphaIou = 0.98f;
                    q.alphaRmse = 6f;
                    q.normalAngleMean = 2.5f;
                    q.normalAngleP95 = 8.0f;
                    q.grayRmse = 5.0f;
                    break;
                case ATOQualityPreset.Custom:
                    // User-owned; untouched / 用户自定义，不覆盖（默认全 1 为近无损）。
                    break;
            }
        }

        /// <summary>Custom-preset factory defaults: every parameter = 1 (near lossless).
        /// Custom 挡位默认参数：全部 = 1（近无损）。</summary>
        public static ATOQualityParams CustomDefaults() => new ATOQualityParams
        {
            msSsim = 1f,
            deltaE = 1f,
            alphaIou = 1f,
            alphaRmse = 1f,
            normalAngleMean = 1f,
            normalAngleP95 = 1f,
            grayRmse = 1f,
        };

        /// <summary>Allowed pixel-density steps / 像素密度挡位。</summary>
        public static readonly int[] DensitySteps = { 512, 1024, 2048, 4096, 8192 };

        public static int SnapDensity(int v)
        {
            int best = DensitySteps[0];
            foreach (var s in DensitySteps)
                if (Mathf.Abs(s - v) < Mathf.Abs(best - v)) best = s;
            return best;
        }
    }
}
