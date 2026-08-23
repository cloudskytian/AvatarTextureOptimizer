using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>EN: Quality measurements after candidate upsampling. ZH: 候选图双线性上采样后的质量测量。</summary>
    internal sealed class QualityResult
    {
        public float Structural = 1f;
        public float DeltaE2000;
        public float AlphaRmse;
        public float CutoutIou = 1f;
        public float NormalMeanDegrees;
        public float NormalP95Degrees;
        public Vector4 ChannelRmse;
        public bool IsPureColor;

        public bool Passes(TextureSemantic semantic, QualityThresholds threshold)
        {
            switch (semantic)
            {
                case TextureSemantic.ColorOpaque:
                    return Structural >= threshold.SsimMinimum && DeltaE2000 <= threshold.DeltaE2000Maximum;
                case TextureSemantic.ColorAlpha:
                    return Structural >= threshold.SsimMinimum && DeltaE2000 <= threshold.DeltaE2000Maximum &&
                           AlphaRmse <= threshold.AlphaRmseMaximum && CutoutIou >= threshold.CutoutIouMinimum;
                case TextureSemantic.Normal:
                    return NormalMeanDegrees <= threshold.NormalMeanDegreesMaximum &&
                           NormalP95Degrees <= threshold.NormalP95DegreesMaximum;
                case TextureSemantic.Grayscale:
                    return Mathf.Max(ChannelRmse.x, ChannelRmse.y, ChannelRmse.z, ChannelRmse.w) <= threshold.GrayscaleRmseMaximum;
                default: return false;
            }
        }
    }
}
