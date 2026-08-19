using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.API
{
    /// <summary>
    /// One texture slot sampled by a material via a mesh UV channel.
    /// 材质通过网格 UV 通道采样到的一张贴图槽。
    /// </summary>
    public sealed class AtoTextureSlot
    {
        public Material Material;
        public string PropertyName;
        public Texture2D Texture;
        public int UvChannel;
        public AtoTextureKind Kind;
        public AtoAlphaMode AlphaMode;
        public float Cutoff;
        public bool IsSrgb;
        public FilterMode FilterMode;
        public ColorSpace ColorSpace;
        /// <summary>Which channels of a packed mask are actually read. null = unknown / all. / 蒙版实际读取的通道，null 表示未知或全部。</summary>
        public bool[] UsedChannels;
        public bool HasIdentitySt = true;
        public AtoSkipReason SkipReason;
        public string SkipDetail;
    }

    /// <summary>
    /// Result of analysing one material (and optionally one animated variant).
    /// 单张材质（及可选的动画变体）的分析结果。
    /// </summary>
    public sealed class AtoShaderAnalysis
    {
        public Material Material;
        public Shader Shader;
        public bool Success;
        public AtoSkipReason SkipReason;
        public string SkipDetail;
        public AtoAlphaMode AlphaMode;
        public float Cutoff;
        public readonly List<AtoTextureSlot> Slots = new List<AtoTextureSlot>();
    }

    /// <summary>
    /// Context given to third-party shader analysers. / 提供给第三方着色器分析器的上下文。
    /// </summary>
    public sealed class AtoShaderAnalyzeContext
    {
        public Material Material;
        public Renderer Renderer;
        public int MaterialSlotIndex;
        /// <summary>Property name → animated or not. / 属性是否被动画修改。</summary>
        public IReadOnlyDictionary<string, bool> AnimatedProperties;
        /// <summary>True when any _ST / ScrollRotate / rotation curve exists. / 是否存在任何 ST / 滚动旋转曲线。</summary>
        public bool HasAnimatedUvTransform;
    }

    /// <summary>
    /// Per-island quality sample after a candidate scale. / 候选缩放后的单岛质量采样。
    /// </summary>
    public struct AtoQualitySample
    {
        public float MsSsim;
        public float DeltaE;
        public float AlphaRmse;
        public float CutoutIou;
        public float NormalMeanDegrees;
        public float NormalP95Degrees;
        public float GrayRmse;
        public bool UsedSingleScaleSsim;
        public bool SkippedSsimForTinyIsland;

        public bool Passes(AtoQualityThresholds t, AtoTextureKind kind, AtoAlphaMode alpha)
        {
            if (t == null) return false;
            if (kind == AtoTextureKind.Normal)
            {
                return NormalMeanDegrees <= t.normalMeanDegrees && NormalP95Degrees <= t.normalP95Degrees;
            }

            if (kind == AtoTextureKind.Gray)
            {
                return GrayRmse <= t.grayRmse;
            }

            if (!SkippedSsimForTinyIsland && MsSsim < t.msSsim) return false;
            if (kind != AtoTextureKind.Gray && DeltaE > t.deltaE) return false;

            if (alpha == AtoAlphaMode.Cutout && CutoutIou < t.cutoutIou) return false;
            if (alpha == AtoAlphaMode.Blend && AlphaRmse > t.alphaRmse) return false;
            return true;
        }
    }
}
