using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    internal static class TextureLodSafety
    {
        internal static bool RequiresFractionalLodFallback(FilterMode filterMode, int candidateMipCount)
        {
            // Runtime trilinear filtering can evaluate every fractional LOD. Passing nonlinear SSIM, DeltaE,
            // cutout and normal-angle thresholds at the two integer endpoints does not prove every interpolation
            // between them. Until the final gate validates those continuous cases conservatively, retain the source.
            // Trilinear 会采样任意分数 LOD；整数端点通过非线性指标不能证明中间值，因此保守保留源资源。
            return filterMode == FilterMode.Trilinear && candidateMipCount > 1;
        }
    }
}
