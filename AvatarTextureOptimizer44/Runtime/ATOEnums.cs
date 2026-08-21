// ATOEnums.cs - Shared enums for Avatar Texture Optimizer. / Avatar贴图优化器的公共枚举。
// Copyright (c) fosa. MIT License.
using System;

namespace Fosa.ATO.Runtime
{
    /// <summary>Quality preset gears. Parameters follow published psychovisual research:
    /// MS-SSIM (Wang, Simoncelli & Bovik 2003/2004), CIEDE2000 JND ~ 1.0-2.3 (Sharma et al. 2005).
    /// 质量预设挡位。参数参考已发表的视觉心理学研究：MS-SSIM（Wang 2003/2004）、CIEDE2000 可觉察差 ~1.0-2.3（Sharma 2005）。</summary>
    public enum ATOQualityPreset
    {
        /// <summary>Near lossless (all 1.0 / strictest). 近乎无损（全部 1.0 / 最严）。</summary>
        NearLossless = 0,
        /// <summary>Default gear: visually lossless in motion. 默认挡：动态下视觉无损。</summary>
        High = 1,
        /// <summary>Barely noticeable loss. 勉强可察觉的损失。</summary>
        Medium = 2,
        /// <summary>Noticeable but acceptable loss. 可察觉但可接受的损失。</summary>
        Low = 3,
        /// <summary>User defined, never overwritten by other gears. 用户自定义，不会被其他挡位覆盖。</summary>
        Custom = 4,
    }

    /// <summary>Pixel density clamp gears (px per real-world meter). / 像素密度钳制挡位（每真实米像素数）。</summary>
    public enum ATOPixelDensity
    {
        Px512 = 512, Px1024 = 1024, Px2048 = 2048, Px4096 = 4096, Px8192 = 8192,
    }

    /// <summary>Minimum island padding option (px). / 岛间最小边距选项（像素）。</summary>
    public enum ATOPadding
    {
        Px4 = 4, Px8 = 8, Px16 = 16, Px32 = 32, Px64 = 64,
    }

    /// <summary>Safe compression format choices. Only formats valid for the current build platform are offered at runtime.
    /// 安全压缩格式选项。运行时只允许选择对当前构建平台有效的格式。</summary>
    public enum ATOCompression
    {
        /// <summary>Pick best format automatically by content & platform. 根据内容与平台自动选择最佳格式。</summary>
        Auto = 0,
        // ---- PC (Windows) block compression / PC 平台块压缩 ----
        BC7 = 1,      // high quality RGB(A) / 高质量 RGB(A)
        DXT5 = 2,     // BC3, legacy RGBA / 旧式 RGBA
        DXT1 = 3,     // BC1, opaque RGB / 不透明 RGB
        BC5 = 4,      // two channel, normal maps / 双通道，法线
        BC4 = 5,      // single channel grayscale / 单通道灰度
        // ---- Mobile ASTC (Android & iOS) / 移动端 ASTC ----
        ASTC_4x4 = 6,
        ASTC_5x5 = 7,
        ASTC_6x6 = 8,
        ASTC_8x8 = 9,
    }

    /// <summary>Texture usage category used to split compression / mipmap options. / 贴图用途分类，用于区分压缩与Mipmap选项。</summary>
    public enum ATOTextureCategory
    {
        Opaque = 0,      // no alpha / 无透明
        Transparent = 1, // has alpha / 有透明
        NormalMap = 2,
        Grayscale = 3,
    }

    /// <summary>Target platform for per-platform overrides. / 平台Override的目标平台。</summary>
    public enum ATOPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2,
    }

    /// <summary>Texture color-space role detected by the analyzer. / 分析器检测到的贴图色彩空间角色。</summary>
    [Flags]
    public enum ATOTextureRole
    {
        None = 0,
        /// <summary>Main (albedo) color sampled by mesh UV. / 主色，网格UV采样。</summary>
        MainColor = 1,
        /// <summary>Normal map (tangent space). / 法线贴图（切线空间）。</summary>
        Normal = 2,
        /// <summary>Mask / lookup driven by mesh UV (grayscale-ish). / 蒙版/查找表（近灰度）。</summary>
        Mask = 4,
        /// <summary>Emission / self illumination. / 自发光。</summary>
        Emission = 8,
        /// <summary>Matcap: sampled with view-space normal UV, NOT mesh UV - never atlased. / Matcap：以视角法线为UV，非网格UV采样——绝不图集化。</summary>
        MatCap = 16,
        /// <summary>Any other linear data texture sampled by mesh UV. / 其他由网格UV采样的线性数据贴图。</summary>
        Data = 32,
    }
}
