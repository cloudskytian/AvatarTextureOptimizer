// Avatar Texture Optimizer / 头像贴图优化器
// Runtime settings model. Serializable on the avatar component.
// 运行时设置模型。所有内容均可序列化在 Avatar 组件上。
//
// NOTE: This assembly cannot reference UnityEditor types, therefore all
//       texture format selections use ATO's own safe enums which are mapped to
//       TextureImporterFormat inside the Editor assembly.
// 注意：本程序集不能引用 UnityEditor 类型，因此所有贴图格式选项均使用 ATO
//       自己的安全枚举，在 Editor 程序集中映射为 TextureImporterFormat。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer
{
    /// <summary>
    /// Quality preset. Switching preset fills the QualitySettings with reference
    /// values from literature/industry practice. <see cref="Custom"/> parameters
    /// are fully user-controlled and never overwritten by preset changes.
    /// 质量挡位。切换挡位会用学术/业内参考值填充质量参数；<see cref="Custom"/>
    /// 挡位的参数完全由用户控制，切换挡位不会覆盖它。
    /// </summary>
    public enum ATOQualityPreset
    {
        /// <summary>Performance first / 性能优先（极低）</summary>
        Performance = 0,
        /// <summary>Low / 低</summary>
        Low = 1,
        /// <summary>Balanced / 中（平衡）</summary>
        Balanced = 2,
        /// <summary>High (default) / 高（默认）</summary>
        High = 3,
        /// <summary>Very high, near lossless scaling / 极高（接近无损的缩放）</summary>
        Maximum = 4,
        /// <summary>Custom, defaults to lossless(1) and never auto-overwritten / 自定义（默认全 1 近无损，永不被覆盖）</summary>
        Custom = 5,
    }

    /// <summary>
    /// Target quality parameters. Thresholds are anchored on common practice:
    /// MS-SSIM ~0.98+ is considered visually lossless, dE2000 ~2.0 is the classic
    /// "just noticeable difference" bound, normal maps are compared by angular
    /// error (mean + p95), cutout by silhouette IoU after clipping, blend alpha by
    /// linear RMSE, grayscale masks by per-used-channel linear RMSE.
    /// 目标质量参数。阈值基于通行实践经验：MS-SSIM≈0.98+ 视为视觉无损，ΔE2000≈2.0
    /// 是经典"刚可察觉差异"界限，法线以角度误差（均值+95 分位）对比，Cutout 以
    /// clip 后轮廓 IoU 对比，Blend 以线性空间 RMSE 对比，灰度蒙版按被使用通道取
    /// 各通道最差 RMSE。
    /// </summary>
    [Serializable]
    public class ATOQualitySettings
    {
        [Tooltip("Overall quality target 0..1. >=0.999 = near-lossless path / 总体质量目标 0..1；>=0.999 走近无损路径")]
        [Range(0.1f, 1f)] public float targetQuality = 0.95f;

        [Tooltip("MS-SSIM minimum (islands with bbox short edge < 176px fall back to single-scale SSIM, < 11px ignored) / MS-SSIM 下限（包围盒短边<176px 的岛回退单尺度 SSIM，<11px 忽略）")]
        [Range(0.5f, 1f)] public float msSsimMin = 0.975f;

        [Tooltip("CIEDE2000 ΔE maximum (mean over island coverage) / CIEDE2000 ΔE 上限（岛覆盖区均值）")]
        [Range(0.1f, 12f)] public float deltaEMax = 2.0f;

        [Tooltip("Normal map mean angular error in degrees / 法线贴图平均角度误差（度）")]
        [Range(0.1f, 15f)] public float normalMeanDegMax = 1.5f;

        [Tooltip("Normal map p95 angular error in degrees / 法线贴图 95 分位角度误差（度）")]
        [Range(0.1f, 30f)] public float normalP95DegMax = 3f;

        [Tooltip("Blend alpha linear RMSE maximum / 半透明 alpha 线性 RMSE 上限")]
        [Range(0.001f, 0.3f)] public float alphaRmseMax = 0.02f;

        [Tooltip("Cutout silhouette IoU minimum after clipping / Cutout clip 后轮廓 IoU 下限")]
        [Range(0.5f, 1f)] public float cutoutIouMin = 0.97f;

        [Tooltip("Grayscale mask per-used-channel linear RMSE maximum / 灰度蒙版被使用通道线性 RMSE 上限")]
        [Range(0.001f, 0.3f)] public float grayRmseMax = 0.02f;

        /// <summary>Creates a deep copy. / 深拷贝。</summary>
        public ATOQualitySettings Clone() => (ATOQualitySettings)MemberwiseClone();

        /// <summary>Lossless values (quality = 1). / 近无损值（质量为 1）。</summary>
        public static ATOQualitySettings Lossless() => new ATOQualitySettings
        {
            targetQuality = 1f,
            msSsimMin = 1f,
            deltaEMax = 0.1f,
            normalMeanDegMax = 0.1f,
            normalP95DegMax = 0.1f,
            alphaRmseMax = 0.001f,
            cutoutIouMin = 1f,
            grayRmseMax = 0.001f,
        };
    }

    /// <summary>Build platform for per-platform overrides. / 平台覆盖用的目标平台。</summary>
    public enum ATOPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2,
    }

    /// <summary>
    /// Texture category used by importer/compression option groups.
    /// 贴图分类：用于导入参数与压缩格式分组。
    /// </summary>
    public enum ATOTextureCategory
    {
        /// <summary>Has meaningful alpha channel / 带 alpha 通道（有实际透明度）</summary>
        Transparent = 0,
        /// <summary>No alpha / 不透明</summary>
        Opaque = 1,
        /// <summary>Normal map / 法线贴图</summary>
        Normal = 2,
        /// <summary>Grayscale / mask / 灰度（蒙版）</summary>
        Grayscale = 3,
    }

    /// <summary>
    /// Safe color-texture formats selectable by the user. Unsafe entries (e.g.
    /// formats without alpha for textures that need alpha) are never offered;
    /// mapping to TextureImporterFormat happens in the Editor assembly.
    /// 可供用户选择的安全彩色贴图格式。不安全项（如需要 alpha 的贴图提供无 alpha
    /// 格式）永远不会出现在选项中；映射到 TextureImporterFormat 在 Editor 程序集完成。
    /// </summary>
    public enum ATOEncodingFormat
    {
        /// <summary>Resolved best default for the platform / 该平台的通用最优解（按平台自动解析）</summary>
        Auto = 0,
        RGBA32 = 1,
        ARGB32 = 2,
        RGB24 = 3,
        DXT1 = 4,
        DXT5 = 5,
        BC7 = 6,
        ASTC_4x4 = 7,
        ASTC_6x6 = 8,
        ASTC_8x8 = 9,
        ETC2_RGBA8 = 10,
        ETC2_RGB4 = 11,
        PVRTC_RGB4 = 12,
        PVRTC_RGBA4 = 13,
        BC5 = 14,
        R8 = 15,
        R16 = 16,
    }

    /// <summary>Per-category importer/compression rule. / 单分类导入与压缩规则。</summary>
    [Serializable]
    public class ATOCategoryRule
    {
        [Tooltip("Compression format (safe enum) / 压缩格式（安全枚举）")]
        public ATOEncodingFormat format = ATOEncodingFormat.Auto;

        [Tooltip("Crunch compression where supported / 支持的格式使用 Crunch 压缩")]
        public bool crunch = false;

        [Tooltip("Compressor quality 0..100 / 压缩质量 0..100")]
        [Range(0, 100)] public int compressorQuality = 50;

        [Tooltip("Enable mipmaps + streaming mipmaps (bound together per VRChat requirement) / 启用 Mipmap 与 MipStreaming（按 VRChat 要求二者绑定）")]
        public bool mipmapsAndStreaming = true;

        public ATOCategoryRule Clone() => (ATOCategoryRule)MemberwiseClone();

        /// <summary>Stable string for cache hashing. / 用于缓存哈希的稳定字符串。</summary>
        public string HashKey()
            => $"{(int)format}|c{(crunch ? 1 : 0)}|q{compressorQuality}|m{(mipmapsAndStreaming ? 1 : 0)}";
    }

    /// <summary>
    /// Per-platform override of all optimization-sensitive parameters.
    /// Defaults are read from the current build platform; when disabled the
    /// platform's built-in best defaults are used.
    /// 单平台覆盖参数。默认值读取当前构建平台；未勾选时使用平台内置最优默认。
    /// </summary>
    [Serializable]
    public class ATOPlatformOverride
    {
        [Tooltip("Enable this platform override / 启用此平台覆盖")]
        public bool enabled = false;

        [Tooltip("Atlas/texture rule: transparent / 透明贴图规则")]
        public ATOCategoryRule transparent = new ATOCategoryRule();

        [Tooltip("Atlas/texture rule: opaque / 不透明贴图规则")]
        public ATOCategoryRule opaque = new ATOCategoryRule();

        [Tooltip("Atlas/texture rule: normal maps / 法线贴图规则")]
        public ATOCategoryRule normal = new ATOCategoryRule();

        [Tooltip("Atlas/texture rule: grayscale masks / 灰度蒙版规则")]
        public ATOCategoryRule grayscale = new ATOCategoryRule();

        [Tooltip("Max atlas edge length; 0 = platform default (PC 8192, mobile 4096) / 图集最大边长；0=平台默认（PC 8192，移动端 4096）")]
        public int maxAtlasSize = 0;

        public ATOCategoryRule RuleFor(ATOTextureCategory cat)
        {
            switch (cat)
            {
                case ATOTextureCategory.Transparent: return transparent;
                case ATOTextureCategory.Opaque: return opaque;
                case ATOTextureCategory.Normal: return normal;
                default: return grayscale;
            }
        }

        public ATOPlatformOverride Clone()
        {
            return new ATOPlatformOverride
            {
                enabled = enabled,
                transparent = transparent.Clone(),
                opaque = opaque.Clone(),
                normal = normal.Clone(),
                grayscale = grayscale.Clone(),
                maxAtlasSize = maxAtlasSize,
            };
        }
    }

    /// <summary>Atlas island padding options (px). / 图集岛间距选项（像素）。</summary>
    public enum ATOAtlasPadding
    {
        Pad4 = 4,
        Pad8 = 8,
        Pad16 = 16,
        Pad32 = 32,
        Pad64 = 64,
    }

    /// <summary>How to pick the UI language. / 界面语言选择方式。</summary>
    public enum ATOLanguageMode
    {
        /// <summary>Follow NDMF's language setting / 跟随 NDMF 当前语言配置</summary>
        Auto = 0,
        /// <summary>Force a language (if translation missing, fall back to English) / 强制语言（缺失时回退英文）</summary>
        Manual = 1,
    }
}
