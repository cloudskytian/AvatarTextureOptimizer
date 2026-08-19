// AvatarTextureOptimizer.cs
// The single component users add to their avatar root. / 用户挂到 Avatar 根上的唯一组件。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>Quality preset tiers. / 质量挡位。</summary>
    public enum QualityPreset
    {
        /// <summary>Near lossless: islands keep original resolution (no resampling). / 近无损:岛保持原分辨率,不重采样。</summary>
        NearLossless = 0,
        High = 1,
        Medium = 2,
        Low = 3,
        /// <summary>User-defined thresholds; never overwritten by other presets. / 自定义阈值,不会被其他挡位覆盖。</summary>
        Custom = 4,
    }

    /// <summary>Pixel density tiers in px/m. / 像素密度挡位(px/米)。</summary>
    public enum DensityTier
    {
        X512 = 512,
        X1024 = 1024,
        X2048 = 2048,
        X4096 = 4096,
        X8192 = 8192,
    }

    /// <summary>Atlas padding options in pixels (island gap). / 图集岛间距选项(像素)。</summary>
    public enum AtlasPadding
    {
        P4 = 4,
        P8 = 8,
        P16 = 16,
        P32 = 32,
        P64 = 64,
    }

    /// <summary>Target platform for per-platform overrides. / 平台覆盖目标。</summary>
    public enum ATOPlatform
    {
        Windows = 0,
        Android = 1,
        iOS = 2,
    }

    /// <summary>
    /// Safe compression format choices offered to the user; the final decision is clamped by
    /// build-time fallbacks. / 提供给用户的安全压缩格式枚举;构建时会再做兜底钳制。
    /// </summary>
    public enum TexFormatChoice
    {
        Auto = 0,
        // PC block formats / PC 块压缩
        BC7 = 1,
        DXT1 = 2,
        DXT1Crunched = 3,
        DXT5 = 4,
        DXT5Crunched = 5,
        BC4 = 6,
        // Mobile ASTC / 移动端
        ASTC4x4 = 10,
        ASTC6x6 = 11,
        ASTC8x8 = 12,
    }

    /// <summary>
    /// Perceptual quality thresholds. A candidate downscale passes only when ALL metrics pass
    /// (worst-of). / 感知质量阈值:所有指标全部达标才算通过(取最差)。
    /// </summary>
    [Serializable]
    public class QualityThresholds
    {
        // === Color metrics (applied on sRGB color textures) / 颜色指标(用于 sRGB 颜色贴图) ===
        [Tooltip("Minimum MS-SSIM (short-edge <176px falls back to single-scale SSIM; <11px ignored)")]
        public float msSsimMin = 0.995f;          // [0..1]
        [Tooltip("Maximum mean CIEDE2000 color difference / 最大平均 CIEDE2000 色差")]
        public float deltaEMax = 1.0f;            // in ΔE00 units
        [Tooltip("Minimum alpha-coverage IoU for Cutout materials / Cutout 材质的 alpha 覆盖 IoU 下限")]
        public float alphaIoUMin = 0.995f;        // [0..1]
        [Tooltip("Maximum linear alpha RMSE for Blend materials / Blend 材质的线性 alpha RMSE 上限")]
        public float alphaRmseMax = 0.006f;       // [0..1]
        // === Normal map metrics / 法线贴图指标 ===
        [Tooltip("Maximum mean angular error in degrees after decode/resample/renormalize / 解码重采样重归一化后的平均角度误差上限(度)")]
        public float normalAngleMeanMax = 1.0f;   // degrees
        [Tooltip("Maximum p95 angular error in degrees / p95 角度误差上限(度)")]
        public float normalAngleP95Max = 3.0f;    // degrees
        // === Grayscale/mask metrics / 灰度蒙版指标 ===
        [Tooltip("Maximum linear-space RMSE per used channel (worst channel governs) / 被使用通道的线性 RMSE 上限(逐通道取最差)")]
        public float grayRmseMax = 0.010f;        // [0..1]

        /// <summary>Near-lossless values (quality == 1). / 近无损值(质量=1)。</summary>
        public static QualityThresholds NearLossless() => new QualityThresholds
        {
            msSsimMin = 1f, deltaEMax = 0f, alphaIoUMin = 1f, alphaRmseMax = 0f,
            normalAngleMeanMax = 0f, normalAngleP95Max = 0f, grayRmseMax = 0f,
        };

        /// <summary>
        /// Preset thresholds. Rationale (industry/academic):
        /// CIEDE2000 JND ≈ 1.0 (Sharma et al. 2005); SSIM/MS-SSIM ≥ 0.99 is commonly treated
        /// as visually lossless (Wang et al. 2004); normal-map angular budgets follow
        /// game-industry normal compression tolerances (mean ≈1°, p95 ≈3° for high quality).
        /// / 挡位阈值。依据:CIEDE2000 可觉差≈1.0;MS-SSIM≥0.99 通常视为视觉无损;
        /// 法线角度预算参考游戏业法线压缩容差。
        /// </summary>
        public static QualityThresholds ForPreset(QualityPreset preset)
        {
            switch (preset)
            {
                case QualityPreset.NearLossless:
                    return NearLossless();
                case QualityPreset.High:
                    return new QualityThresholds
                    {
                        msSsimMin = 0.995f, deltaEMax = 1.0f, alphaIoUMin = 0.995f, alphaRmseMax = 0.006f,
                        normalAngleMeanMax = 1.0f, normalAngleP95Max = 3.0f, grayRmseMax = 0.010f,
                    };
                case QualityPreset.Medium:
                    return new QualityThresholds
                    {
                        msSsimMin = 0.98f, deltaEMax = 2.5f, alphaIoUMin = 0.99f, alphaRmseMax = 0.015f,
                        normalAngleMeanMax = 2.0f, normalAngleP95Max = 6.0f, grayRmseMax = 0.025f,
                    };
                case QualityPreset.Low:
                    return new QualityThresholds
                    {
                        msSsimMin = 0.95f, deltaEMax = 5.0f, alphaIoUMin = 0.98f, alphaRmseMax = 0.030f,
                        normalAngleMeanMax = 4.0f, normalAngleP95Max = 12.0f, grayRmseMax = 0.050f,
                    };
                default:
                    return NearLossless(); // Custom defaults to near-lossless / 自定义默认近无损
            }
        }

        public QualityThresholds Clone() => (QualityThresholds)MemberwiseClone();

        /// <summary>Effectively lossless: skip UV scaling entirely. / 等效无损:完全跳过 UV 缩放。</summary>
        public bool IsNearLossless =>
            msSsimMin >= 0.9999f && deltaEMax <= 0.05f && alphaIoUMin >= 0.9999f &&
            alphaRmseMax <= 0.001f && normalAngleMeanMax <= 0.05f && normalAngleP95Max <= 0.2f &&
            grayRmseMax <= 0.001f;
    }

    /// <summary>One platform profile: all optimization parameters can be overridden per platform. / 单个平台配置:所有优化参数均可按平台覆盖。</summary>
    [Serializable]
    public class PlatformProfile
    {
        public QualityThresholds thresholds = new QualityThresholds();
        public DensityTier minDensity = DensityTier.X2048;
        public DensityTier maxDensity = DensityTier.X4096;
        public bool generateAtlas = true;
        public AtlasPadding padding = AtlasPadding.P4;
        public bool experimentalNpotAtlas = false;
        public TexFormatChoice opaqueFormat = TexFormatChoice.Auto;
        public TexFormatChoice alphaFormat = TexFormatChoice.Auto;
        public TexFormatChoice normalFormat = TexFormatChoice.Auto;
        public TexFormatChoice grayFormat = TexFormatChoice.Auto;
        public bool mipStreamingOpaque = true;
        public bool mipStreamingAlpha = true;
        public bool mipStreamingNormal = true;
        public bool mipStreamingGray = true;
    }

    /// <summary>
    /// Avatar Texture Optimizer component. Exactly one per avatar, must sit on the same
    /// GameObject as the VRCAvatarDescriptor. / ATO 组件:每个 Avatar 仅一个,必须挂在
    /// VRCAvatarDescriptor 同一物体上。
    /// </summary>
    [AddComponentMenu("Avatar Texture Optimizer/ATO Avatar Texture Optimizer")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/fosa/AvatarTextureOptimizer")]
    public class AvatarTextureOptimizer : MonoBehaviour, VRC.SDKBase.IEditorOnly
    {
        // ------------------------------------------------------------------ //
        // Basic / 基础
        // ------------------------------------------------------------------ //
        [Tooltip("Generate atlases. Unchecked = only whole-texture scaling + import-parameter optimization. / 是否生成图集;不勾选则只做整图缩放与导入参数优化")]
        public bool generateAtlas = true;

        [Tooltip("Quality preset. Switching a preset refreshes thresholds; Custom is never overwritten. / 质量挡位;切换会刷新阈值,Custom 不会被覆盖")]
        public QualityPreset qualityPreset = QualityPreset.High;

        /// <summary>Live threshold values (kept in sync with the preset by the inspector). / 当前阈值(由 Inspector 与挡位联动)。</summary>
        public QualityThresholds thresholds = new QualityThresholds();

        [Tooltip("Objects whose referenced textures skip ALL optimization. / 其引用的全部贴图跳过所有优化的白名单对象")]
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        // ------------------------------------------------------------------ //
        // Advanced (folded by default in UI) / 高级选项(默认折叠)
        // ------------------------------------------------------------------ //
        [Tooltip("Minimum island pixel density (px/m). / 岛最小像素密度(px/米)")]
        public DensityTier minDensity = DensityTier.X2048;
        [Tooltip("Maximum island pixel density (px/m). / 岛最大像素密度(px/米)")]
        public DensityTier maxDensity = DensityTier.X4096;

        [Tooltip("Atlas padding (island gap). Effective padding = max(chosen, ceil(maxEdge/128)). / 图集岛间距;实际取 max(所选, ceil(最大边/128))")]
        public AtlasPadding padding = AtlasPadding.P4;

        [Tooltip("EXPERIMENTAL: allow non-power-of-two atlas sizes (64px step). / 实验性:允许非2次幂图集边长(64px步进)")]
        public bool experimentalNpotAtlas = false;

        [Tooltip("Deduplicate identical textures/atlases (content + parameters). / 去重内容与参数完全相同的贴图/图集")]
        public bool dedupeTextures = true;
        [Tooltip("Deduplicate identical materials and merge mergeable slots. / 去重相同材质并合并可合并的材质槽")]
        public bool dedupeMaterials = true;

        // ------------------------------------------------------------------ //
        // Platform overrides / 平台覆盖
        // ------------------------------------------------------------------ //
        [Tooltip("Override optimization parameters for the Windows(PC) build target. / Windows 平台参数覆盖")]
        public bool overrideWindows = false;
        public PlatformProfile windowsProfile = new PlatformProfile();
        [Tooltip("Override optimization parameters for the Android(Quest/mobile) target. / Android 平台参数覆盖")]
        public bool overrideAndroid = false;
        public PlatformProfile androidProfile = new PlatformProfile();
        [Tooltip("Override optimization parameters for the iOS target. / iOS 平台参数覆盖")]
        public bool overrideiOS = false;
        public PlatformProfile iosProfile = new PlatformProfile();

        // ------------------------------------------------------------------ //
        // Debug / 调试
        // ------------------------------------------------------------------ //
        [Tooltip("Verbose [ATO] logging with per-stage timings. / 详细 [ATO] 日志(含各阶段耗时)")]
        public bool verboseLogging = false;
        [Tooltip("Save generated atlases as PNG next to the avatar for inspection. / 将生成图集存为 PNG 便于检查")]
        public bool debugSaveAtlases = false;

        /// <summary>Resolution limit for the given platform (mobile = 4096). / 平台最大图集边长(移动端 4096)。</summary>
        public static int MaxAtlasEdge(ATOPlatform p) => p == ATOPlatform.Windows ? 8192 : 4096;

        /// <summary>Safe format choices for a platform. / 平台允许的格式枚举。</summary>
        public static TexFormatChoice[] SafeFormats(ATOPlatform p) => p == ATOPlatform.Windows
            ? new[] { TexFormatChoice.Auto, TexFormatChoice.BC7, TexFormatChoice.DXT1, TexFormatChoice.DXT1Crunched, TexFormatChoice.DXT5, TexFormatChoice.DXT5Crunched, TexFormatChoice.BC4 }
            : new[] { TexFormatChoice.Auto, TexFormatChoice.ASTC4x4, TexFormatChoice.ASTC6x6, TexFormatChoice.ASTC8x8 };

        /// <summary>Resolve the effective profile for a platform (global values where not overridden). / 解析平台生效配置(未覆盖时用全局值)。</summary>
        public PlatformProfile Resolve(ATOPlatform platform)
        {
            bool useOverride = platform == ATOPlatform.Windows ? overrideWindows
                : platform == ATOPlatform.Android ? overrideAndroid
                : overrideiOS;
            if (!useOverride) return MergeGlobalDefaults(platform);

            var src = platform == ATOPlatform.Windows ? windowsProfile
                : platform == ATOPlatform.Android ? androidProfile
                : iosProfile;
            var merged = src.Clone();
            return merged;
        }

        /// <summary>Merge: platform-specific hard limits applied over global values. / 合并:平台硬限制套在全局值上。</summary>
        public PlatformProfile MergeGlobalDefaults(ATOPlatform platform)
        {
            var p = new PlatformProfile
            {
                thresholds = thresholds != null ? thresholds.Clone() : new QualityThresholds(),
                minDensity = minDensity,
                maxDensity = maxDensity,
                generateAtlas = generateAtlas,
                padding = padding,
                experimentalNpotAtlas = experimentalNpotAtlas,
                opaqueFormat = TexFormatChoice.Auto,
                alphaFormat = TexFormatChoice.Auto,
                normalFormat = TexFormatChoice.Auto,
                grayFormat = TexFormatChoice.Auto,
                mipStreamingOpaque = true,
                mipStreamingAlpha = true,
                mipStreamingNormal = true,
                mipStreamingGray = true,
            };
            return p;
        }
    }

    /// <summary>Small reflection-free clone helper. / 简单克隆辅助。</summary>
    internal static class CloneEx
    {
        internal static PlatformProfile Clone(this PlatformProfile p) => new PlatformProfile
        {
            thresholds = p.thresholds.Clone(),
            minDensity = p.minDensity,
            maxDensity = p.maxDensity,
            generateAtlas = p.generateAtlas,
            padding = p.padding,
            experimentalNpotAtlas = p.experimentalNpotAtlas,
            opaqueFormat = p.opaqueFormat,
            alphaFormat = p.alphaFormat,
            normalFormat = p.normalFormat,
            grayFormat = p.grayFormat,
            mipStreamingOpaque = p.mipStreamingOpaque,
            mipStreamingAlpha = p.mipStreamingAlpha,
            mipStreamingNormal = p.mipStreamingNormal,
            mipStreamingGray = p.mipStreamingGray,
        };
    }
}
