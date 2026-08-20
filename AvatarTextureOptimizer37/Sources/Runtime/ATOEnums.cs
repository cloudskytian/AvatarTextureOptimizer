// ============================================================================
// ATO enums and small value types
// ATO 枚举与小型值类型
// ============================================================================

#region

using System;

#endregion

namespace net.fosa.AvatarTextureOptimizer
{
    /// <summary>Quality tiers. The effective metric thresholds are derived
    /// from the tier's quality value (see <see cref="ATOQualityParams.FromQuality"/>).
    /// 质量档位。有效指标阈值由档位的质量值推导（见 ATOQualityParams.FromQuality）。</summary>
    public enum ATOQualityTier
    {
        /// <summary>Lossless / near-lossless (quality = 1). UV scaling of the
        /// affected texture types is skipped entirely; islands are copied.
        /// 近无损（质量=1）。跳过对应贴图类型岛的 UV 缩放（含纯色岛），原样拷贝。</summary>
        Lossless = 0,
        /// <summary>High (quality ≈ 0.95). 高（质量≈0.95）。</summary>
        High = 1,
        /// <summary>Medium (quality ≈ 0.90). Default tier. 中（质量≈0.90）。默认档。</summary>
        Medium = 2,
        /// <summary>Low (quality ≈ 0.80). 低（质量≈0.80）。</summary>
        Low = 3,
        /// <summary>Extreme (quality ≈ 0.70). 极限（质量≈0.70）。</summary>
        Extreme = 4,
        /// <summary>Custom: raw parameters from
        /// <see cref="ATOComponent.CustomParams"/> (default all-lossless).
        /// 自定义：使用组件上的原始参数（默认近无损）。</summary>
        Custom = 5,
    }

    /// <summary>Texture category used for per-category switches (mips, formats,
    /// quality grouping).
    /// 用于分类开关（mips/格式/质量分组）的贴图类别。</summary>
    public enum ATOTextureCategory
    {
        /// <summary>Albedo/base color used by opaque materials. 不透明材质主色。</summary>
        Opaque = 0,
        /// <summary>Albedo used by cutout/blend/premultiply materials.
        /// 透明（裁剪/混合/预乘）材质主色。</summary>
        Transparent = 1,
        /// <summary>Normal maps. 法线贴图。</summary>
        Normal = 2,
        /// <summary>Single-channel masks / lightmaps etc. 单通道蒙版等。</summary>
        Gray = 3,
    }

    /// <summary>Safe compression format choices. The editor filters this list
    /// per platform, per NPOT flag and per channel requirements; anything
    /// unsafe is never selected (fallback + console warning instead).
    /// 安全压缩格式选项。编辑器按平台/NPOT/通道需求过滤；任何不安全选择都不会
    /// 生效（改为安全回退并在控制台警告）。</summary>
    public enum ATOFormatChoice
    {
        /// <summary>Automatic: best quality/size for the category + platform.
        /// 自动：按类别+平台取最优。</summary>
        Auto = 0,
        // BC (PC / D3D) BC（PC / D3D）
        BC1 = 10,   /// <summary>BC1/DXT1 - RGB(A), no alpha (or 1-bit). 无有效 alpha。</summary>
        BC3 = 11,   /// <summary>BC3/DXT5 - RGBA. RGBA。</summary>
        BC4 = 12,   /// <summary>BC4 - single channel R. 单通道 R。</summary>
        BC5 = 13,   /// <summary>BC5 - RG (normal). 法线 RG。</summary>
        BC7 = 14,   /// <summary>BC7 - best quality RGBA. 高质量 RGBA。</summary>
        // ETC / EAC (Android GLES)
        ETC2 = 20,  /// <summary>ETC2 - RGB. RGB。</summary>
        ETC2A = 21, /// <summary>ETC2 + 1-bit A. ETC2+1bit A。</summary>
        ETC2A8 = 22,/// <summary>ETC2 + 8-bit A. ETC2+8bit A。</summary>
        EACR = 23,  /// <summary>EAC R11. 单通道 R。</summary>
        EACRG = 24, /// <summary>EAC RG11 (normal). 法线 RG。</summary>
        // PVRTC (iOS legacy; NOT NPOT) PVRTC（iOS 旧格式；不支持 NPOT）
        PVRTC2 = 30,/// <summary>PVRTC 2bpp RGBA (POT only). 仅 POT。</summary>
        PVRTC4 = 31,/// <summary>PVRTC 4bpp RGBA (POT only). 仅 POT。</summary>
        // ASTC (all modern platforms; NPOT-safe) ASTC（现代平台通用；NPOT 安全）
        ASTC4x4 = 40, /// <summary>ASTC 4x4 (highest). 最高。</summary>
        ASTC5x5 = 41, /// <summary>ASTC 5x5.</summary>
        ASTC6x6 = 42, /// <summary>ASTC 6x6 (balanced). 均衡。</summary>
        ASTC8x8 = 43, /// <summary>ASTC 8x8 (smallest). 最小。</summary>
        // Raw 原始
        RGB24 = 50, /// <summary>24-bit RGB (uncompressed). 无压缩 RGB。</summary>
        RGBA32 = 51,/// <summary>32-bit RGBA (uncompressed). 无压缩 RGBA。</summary>
        Alpha8 = 52,/// <summary>8-bit alpha only. 仅 alpha。</summary>
    }

    /// <summary>Build platforms recognized by ATO (mirrors Unity build targets
    /// grouped for VRChat avatars).
    /// ATO 识别的构建平台（VRChat Avatar 对应的 Unity 构建目标分组）。</summary>
    public enum ATOPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2,
    }

    [Flags]
    /// <summary>Logging categories for [ATO] console output.
    /// [ATO] 控制台日志类别。</summary>
    public enum ATOLogMask
    {
        None = 0,
        /// <summary>Analysis phase (scan/UV/island/dedup input). 分析阶段。</summary>
        Analysis = 1 << 0,
        /// <summary>Quality evaluation / island scaling. 质量评估/岛缩放。</summary>
        Quality = 1 << 1,
        /// <summary>Atlas packing. 装箱。</summary>
        Packing = 1 << 2,
        /// <summary>Atlas composition & UV remap. 图集合成与 UV 重映射。</summary>
        Atlas = 1 << 3,
        /// <summary>Texture import / material parameter application.
        /// 纹理导入/材质参数应用。</summary>
        Import = 1 << 4,
        /// <summary>Final dedup / sub-mesh merge. 最终去重/子网格合并。</summary>
        Dedup = 1 << 5,
        /// <summary>Extra per-step detail lines. 每步额外细节。</summary>
        Verbose = 1 << 6,
    }
}
