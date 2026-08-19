using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.API
{
    /// <summary>
    /// Optional hook that can veto or tighten a quality decision.
    /// 可选钩子，可否决或收紧质量判定。
    /// </summary>
    public interface IAtoQualityHook
    {
        string Id { get; }

        /// <summary>
        /// Return false to force the candidate scale to fail. / 返回 false 则该候选缩放视为不通过。
        /// </summary>
        bool Accept(Texture2D source, AtoTextureKind kind, AtoQualitySample sample, AtoQualityThresholds thresholds);
    }
}
