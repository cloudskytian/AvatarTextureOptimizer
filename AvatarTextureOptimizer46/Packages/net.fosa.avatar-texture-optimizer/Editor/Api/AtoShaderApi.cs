// SPDX-License-Identifier: MIT
// EN: Public extension points for shader analysis. Third party shader authors implement these to make
//     their shaders optimizable without patching ATO.
// ZH: 着色器分析的公共扩展点。第三方着色器作者实现这些接口即可让自己的着色器被优化，无需修改 ATO。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Api
{
    /// <summary>
    /// EN: How a shader derives the UV used to sample a texture.
    /// ZH: 着色器推导采样某贴图所用 UV 的方式。
    /// </summary>
    public enum AtoSamplingSpace
    {
        /// <summary>EN: Sampled with a mesh UV channel, unmodified. Optimizable. ZH: 使用未经修改的网格 UV 通道采样。可优化。</summary>
        MeshUV = 0,
        /// <summary>EN: Sampled in a space ATO cannot follow (matcap, screen, panorama, gradient LUT, decal...). ZH: 在 ATO 无法跟踪的空间中采样（matcap、屏幕、全景、渐变 LUT、贴花等）。</summary>
        NonMeshUV = 1,
        /// <summary>EN: Unknown. Treated exactly like <see cref="NonMeshUV"/>. ZH: 未知。与 <see cref="NonMeshUV"/> 同等对待。</summary>
        Unknown = 2,
    }

    /// <summary>
    /// EN: Description of one texture reference found on a material.
    /// ZH: 在材质上找到的一个贴图引用的描述。
    /// </summary>
    public sealed class AtoTextureRef
    {
        /// <summary>EN: Shader property name, e.g. "_MainTex". ZH: 着色器属性名，例如 "_MainTex"。</summary>
        public string PropertyName;
        /// <summary>EN: The referenced texture. ZH: 被引用的贴图。</summary>
        public Texture Texture;
        /// <summary>EN: Mesh UV channel index (0..7) when <see cref="Space"/> is MeshUV. ZH: 当 <see cref="Space"/> 为 MeshUV 时的网格 UV 通道索引（0..7）。</summary>
        public int UvChannel;
        /// <summary>EN: Sampling space. ZH: 采样空间。</summary>
        public AtoSamplingSpace Space = AtoSamplingSpace.Unknown;
        /// <summary>EN: Semantic kind, used for metric selection and compression. ZH: 语义分类，用于选择度量方式与压缩格式。</summary>
        public AtoTextureKind Kind = AtoTextureKind.ColorOpaque;
        /// <summary>EN: Channels actually consumed by the shader, as an RGBA bit mask. ZH: 着色器实际使用的通道，RGBA 位掩码。</summary>
        public int UsedChannelMask = 0xF;
        /// <summary>EN: True when the shader ignores tiling/offset for this property. ZH: 着色器忽略该属性的 tiling/offset 时为 true。</summary>
        public bool IgnoresScaleOffset;
    }

    /// <summary>
    /// EN: Result of analyzing one material.
    /// ZH: 分析单个材质的结果。
    /// </summary>
    public sealed class AtoMaterialAnalysis
    {
        /// <summary>EN: All texture references. ZH: 全部贴图引用。</summary>
        public readonly List<AtoTextureRef> Textures = new List<AtoTextureRef>();
        /// <summary>EN: Alpha handling of the material. ZH: 材质的 alpha 处理方式。</summary>
        public AtoAlphaMode AlphaMode = AtoAlphaMode.Opaque;
        /// <summary>EN: Alpha cutoff when <see cref="AlphaMode"/> is Cutout. ZH: 当 <see cref="AlphaMode"/> 为 Cutout 时的裁剪阈值。</summary>
        public float Cutoff = 0.5f;
        /// <summary>EN: When set, the whole material must be treated as whitelisted. ZH: 设置后整个材质必须按白名单处理。</summary>
        public bool ForceWhitelist;
        /// <summary>EN: Human readable reason for <see cref="ForceWhitelist"/>. ZH: <see cref="ForceWhitelist"/> 的可读原因。</summary>
        public string ForceWhitelistReason;
    }

    /// <summary>
    /// EN: Implement and register through <see cref="AtoShaderAnalyzerRegistry"/> to teach ATO about a shader.
    /// ZH: 实现该接口并通过 <see cref="AtoShaderAnalyzerRegistry"/> 注册，即可让 ATO 认识某个着色器。
    /// </summary>
    public interface IAtoShaderAnalyzer
    {
        /// <summary>EN: Higher priority analyzers are consulted first. ZH: 优先级更高的分析器会被优先询问。</summary>
        int Priority { get; }
        /// <summary>EN: Returns true when this analyzer understands the shader. ZH: 该分析器能理解该着色器时返回 true。</summary>
        bool CanAnalyze(Shader shader);
        /// <summary>EN: Analyzes a material. Return null to fall through to the next analyzer. ZH: 分析一个材质。返回 null 则交由下一个分析器处理。</summary>
        AtoMaterialAnalysis Analyze(Material material);
    }

    /// <summary>
    /// EN: Registry of shader analyzers.
    /// ZH: 着色器分析器注册表。
    /// </summary>
    public static class AtoShaderAnalyzerRegistry
    {
        private static readonly List<IAtoShaderAnalyzer> _analyzers = new List<IAtoShaderAnalyzer>();

        /// <summary>EN: Registers an analyzer. Safe to call from [InitializeOnLoadMethod]. ZH: 注册一个分析器。可安全地从 [InitializeOnLoadMethod] 调用。</summary>
        public static void Register(IAtoShaderAnalyzer analyzer)
        {
            if (analyzer == null) throw new ArgumentNullException(nameof(analyzer));
            if (_analyzers.Contains(analyzer)) return;
            _analyzers.Add(analyzer);
            _analyzers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        /// <summary>EN: Unregisters an analyzer. ZH: 注销一个分析器。</summary>
        public static void Unregister(IAtoShaderAnalyzer analyzer) => _analyzers.Remove(analyzer);

        /// <summary>EN: All registered analyzers, highest priority first. ZH: 所有已注册的分析器，优先级从高到低。</summary>
        public static IReadOnlyList<IAtoShaderAnalyzer> Analyzers => _analyzers;
    }
}
