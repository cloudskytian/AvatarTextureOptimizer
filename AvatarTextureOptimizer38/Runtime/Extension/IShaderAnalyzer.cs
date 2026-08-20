using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// How a shader samples a texture. / 着色器采样贴图的方式。
    /// </summary>
    public enum TextureUsageKind
    {
        Albedo = 0,
        Normal = 1,
        Mask = 2,
        Gray = 3,
        Emission = 4,
        Unknown = 5,
        SpecialDeforming = 6
    }

    /// <summary>
    /// Alpha / blend evaluation mode for quality. / 质量评估用的透明模式。
    /// </summary>
    public enum AlphaEvalMode
    {
        Opaque = 0,
        Cutout = 1,
        Blend = 2
    }

    /// <summary>
    /// One texture slot discovered on a material. / 材质上发现的一个贴图槽。
    /// </summary>
    public sealed class ShaderTextureSlot
    {
        public string PropertyName;
        public TextureUsageKind Usage;
        public int UvChannel;
        /// <summary>True if ST/scroll/decal/triplanar/etc. would break atlas. / 存在变换或特殊用途则不可优化。</summary>
        public bool HasUnsafeTransform;
        public string UnsafeReason;
        public ColorSpace ImpliedColorSpace;
        public bool IsNormal;
        public bool IsMask;
        public bool IsGray;
        /// <summary>Which color channels are actually read. Empty = all. / 实际读取的通道，空=全部。</summary>
        public string UsedChannels;
    }

    /// <summary>
    /// Result of analyzing one material. / 分析单个材质的结果。
    /// </summary>
    public sealed class ShaderAnalysisResult
    {
        public bool Supported = true;
        public string UnsupportedReason;
        public readonly List<ShaderTextureSlot> Slots = new List<ShaderTextureSlot>();
        public AlphaEvalMode AlphaMode = AlphaEvalMode.Opaque;
        public float Cutoff = 0.5f;
        public bool CutoffAnimated;
        public bool AlphaModeAnimated;
    }

    /// <summary>
    /// Third-party / advanced shader analyzer. Register via ShaderAnalyzerRegistry.
    /// 第三方/高级着色器分析器。通过 ShaderAnalyzerRegistry 注册。
    /// Higher Priority runs first. / Priority 越大越先执行。
    /// </summary>
    public interface IShaderAnalyzer
    {
        int Priority { get; }
        string Name { get; }

        /// <summary>
        /// Return false to let the next analyzer try. / 返回 false 则交给下一个分析器。
        /// </summary>
        bool TryAnalyze(Material material, out ShaderAnalysisResult result);
    }
}
