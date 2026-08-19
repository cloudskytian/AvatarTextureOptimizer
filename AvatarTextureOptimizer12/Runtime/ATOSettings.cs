// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Serialized settings model.
// AvatarTextureOptimizer (ATO) - 序列化设置模型。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// EN: The concrete numeric quality thresholds used by the target-quality algorithm.
    ///     All thresholds are evaluated on the *decoded, linear-space* image; the worst metric wins.
    /// ZH: 目标质量算法使用的具体数值阈值。
    ///     所有阈值都在“解码后的线性空间”图像上评估，取所有指标中最差的一项作为判定结果。
    /// </summary>
    [Serializable]
    public class ATOQualityParams
    {
        // ---- Structural similarity (colour) / 结构相似度（彩色） ----

        /// <summary>
        /// EN: Minimum MS-SSIM. Islands whose original bounding-box short side is &lt; 176 px fall back to
        ///     single-scale SSIM; &lt; 11 px ignore this metric entirely.
        /// ZH: MS-SSIM 最小值。原尺寸包围盒短边 &lt; 176px 的岛回退到单尺度 SSIM；&lt; 11px 直接忽略此参数。
        /// </summary>
        [Range(0f, 1f)] public float msSsimMin = 0.985f;

        /// <summary>
        /// EN: Maximum mean CIEDE2000 colour difference. 1.0 is roughly one JND.
        /// ZH: CIEDE2000 平均色差上限。1.0 大约是一个恰可察觉差异（JND）。
        /// </summary>
        [Min(0f)] public float deltaE2000Mean = 1.5f;

        /// <summary>EN: Maximum 95th-percentile CIEDE2000. ZH: CIEDE2000 的 95 分位上限。</summary>
        [Min(0f)] public float deltaE2000P95 = 3.0f;

        // ---- Alpha / 透明度 ----

        /// <summary>EN: Minimum silhouette IoU after clip() for Cutout materials. ZH: Cutout 材质 clip 后轮廓 IoU 下限。</summary>
        [Range(0f, 1f)] public float alphaCutoutIoUMin = 0.995f;

        /// <summary>EN: Maximum linear RMSE of the alpha channel for Blend materials. ZH: Blend 材质 alpha 通道线性 RMSE 上限。</summary>
        [Min(0f)] public float alphaBlendRmseMax = 0.010f;

        // ---- Normal maps / 法线贴图 ----

        /// <summary>EN: Maximum mean angular error in degrees after decode/resample/renormalise/encode.
        ///     ZH: 正确解码、重采样、重归一化、编码后的平均角度误差上限（度）。</summary>
        [Min(0f)] public float normalAngleMeanMaxDeg = 1.5f;

        /// <summary>EN: Maximum 95th-percentile angular error in degrees. ZH: 角度误差 95 分位上限（度）。</summary>
        [Min(0f)] public float normalAngleP95MaxDeg = 4.0f;

        // ---- Grayscale / data maps / 灰度与数据贴图 ----

        /// <summary>EN: Maximum per-channel linear RMSE, evaluated only on channels that are actually sampled.
        ///     ZH: 逐通道线性 RMSE 上限，仅在实际被使用的通道上评估，取最差通道。</summary>
        [Min(0f)] public float grayscaleRmseMax = 0.010f;

        // ---- Density clamps / 像素密度钳制 ----

        /// <summary>EN: Lower bound of texel density (texels per meter of world-space surface).
        ///     ZH: 像素密度下限（世界空间每米表面对应的贴图像素数）。</summary>
        public ATOPixelDensity minPixelDensity = ATOPixelDensity.Px2048;

        /// <summary>EN: Upper bound of texel density. ZH: 像素密度上限。</summary>
        public ATOPixelDensity maxPixelDensity = ATOPixelDensity.Px4096;

        /// <summary>EN: When true, no rescaling at all happens (bit-exact copy of the used region).
        ///     ZH: 为 true 时完全不做缩放，直接原样拷贝被使用的区域（不重采样）。</summary>
        public bool lossless = false;

        public ATOQualityParams Clone()
        {
            return (ATOQualityParams)MemberwiseClone();
        }

        /// <summary>
        /// EN: Built-in tier presets. Values are grounded in common image-quality literature:
        ///     MS-SSIM ~0.99+ is generally reported as visually lossless, ~0.95 as good;
        ///     CIEDE2000 &lt;= 1 is one JND, &lt;= 2 "perceptible only on close inspection",
        ///     &lt;= 3.5 is the classic print-acceptance limit.
        /// ZH: 内置挡位预设。取值依据常见图像质量研究结论：
        ///     MS-SSIM 约 0.99 以上普遍被认为视觉无损，约 0.95 为良好；
        ///     CIEDE2000 &lt;= 1 为一个 JND，&lt;= 2 为“仔细看才能察觉”，&lt;= 3.5 是经典印刷可接受上限。
        /// </summary>
        public static ATOQualityParams ForTier(ATOQualityTier tier)
        {
            switch (tier)
            {
                case ATOQualityTier.Draft:
                    return new ATOQualityParams
                    {
                        msSsimMin = 0.900f,
                        deltaE2000Mean = 4.0f,
                        deltaE2000P95 = 8.0f,
                        alphaCutoutIoUMin = 0.970f,
                        alphaBlendRmseMax = 0.045f,
                        normalAngleMeanMaxDeg = 8.0f,
                        normalAngleP95MaxDeg = 18.0f,
                        grayscaleRmseMax = 0.045f,
                        minPixelDensity = ATOPixelDensity.Px512,
                        maxPixelDensity = ATOPixelDensity.Px1024,
                        lossless = false,
                    };
                case ATOQualityTier.Performance:
                    return new ATOQualityParams
                    {
                        msSsimMin = 0.950f,
                        deltaE2000Mean = 2.5f,
                        deltaE2000P95 = 5.0f,
                        alphaCutoutIoUMin = 0.985f,
                        alphaBlendRmseMax = 0.025f,
                        normalAngleMeanMaxDeg = 4.0f,
                        normalAngleP95MaxDeg = 9.0f,
                        grayscaleRmseMax = 0.025f,
                        minPixelDensity = ATOPixelDensity.Px1024,
                        maxPixelDensity = ATOPixelDensity.Px2048,
                        lossless = false,
                    };
                case ATOQualityTier.Balanced:
                default:
                    return new ATOQualityParams
                    {
                        msSsimMin = 0.985f,
                        deltaE2000Mean = 1.5f,
                        deltaE2000P95 = 3.0f,
                        alphaCutoutIoUMin = 0.995f,
                        alphaBlendRmseMax = 0.010f,
                        normalAngleMeanMaxDeg = 1.5f,
                        normalAngleP95MaxDeg = 4.0f,
                        grayscaleRmseMax = 0.010f,
                        minPixelDensity = ATOPixelDensity.Px2048,
                        maxPixelDensity = ATOPixelDensity.Px4096,
                        lossless = false,
                    };
                case ATOQualityTier.High:
                    return new ATOQualityParams
                    {
                        msSsimMin = 0.995f,
                        deltaE2000Mean = 0.8f,
                        deltaE2000P95 = 1.6f,
                        alphaCutoutIoUMin = 0.999f,
                        alphaBlendRmseMax = 0.004f,
                        normalAngleMeanMaxDeg = 0.7f,
                        normalAngleP95MaxDeg = 1.8f,
                        grayscaleRmseMax = 0.004f,
                        minPixelDensity = ATOPixelDensity.Px2048,
                        maxPixelDensity = ATOPixelDensity.Px8192,
                        lossless = false,
                    };
                case ATOQualityTier.Lossless:
                case ATOQualityTier.Custom:
                    return new ATOQualityParams
                    {
                        msSsimMin = 1.0f,
                        deltaE2000Mean = 0.0f,
                        deltaE2000P95 = 0.0f,
                        alphaCutoutIoUMin = 1.0f,
                        alphaBlendRmseMax = 0.0f,
                        normalAngleMeanMaxDeg = 0.0f,
                        normalAngleP95MaxDeg = 0.0f,
                        grayscaleRmseMax = 0.0f,
                        minPixelDensity = ATOPixelDensity.Px4096,
                        maxPixelDensity = ATOPixelDensity.Px8192,
                        lossless = true,
                    };
            }
        }
    }

    /// <summary>
    /// EN: Per-texture-class output settings (compression + mipmaps).
    /// ZH: 按贴图类别划分的输出设置（压缩格式 + Mipmap）。
    /// </summary>
    [Serializable]
    public class ATOTextureClassSettings
    {
        public ATOCompressionFormat format = ATOCompressionFormat.Auto;

        /// <summary>
        /// EN: Compression quality 0..100, forwarded to the texture compressor when the format supports it.
        /// ZH: 压缩质量 0..100，当格式支持时传给压缩器。
        /// </summary>
        [Range(0, 100)] public int compressionQuality = 100;

        /// <summary>
        /// EN: Mipmaps + streaming mipmaps. VRChat requires streaming mipmaps whenever mipmaps are enabled,
        ///     so ATO binds the two together and exposes a single toggle.
        /// ZH: Mipmap 与 MipStreaming。VRChat 要求开启 Mipmap 时必须开启 MipStreaming，
        ///     因此 ATO 将二者绑定，只提供一个开关。
        /// </summary>
        public bool mipmapAndStreaming = true;

        public ATOTextureClassSettings Clone() => (ATOTextureClassSettings)MemberwiseClone();
    }

    /// <summary>
    /// EN: Complete set of parameters that can be overridden per platform.
    /// ZH: 可以按平台覆盖的完整参数集合。
    /// </summary>
    [Serializable]
    public class ATOPlatformSettings
    {
        /// <summary>EN: Only meaningful on override entries. ZH: 仅在覆盖条目上有意义。</summary>
        public bool enabled = false;

        public ATOPlatform platform = ATOPlatform.PC;

        // ---- Quality / 质量 ----
        public ATOQualityTier qualityTier = ATOQualityTier.Balanced;

        /// <summary>EN: Resolved parameters shown in the advanced foldout. ZH: 高级选项折叠区展示的具体参数。</summary>
        public ATOQualityParams quality = ATOQualityParams.ForTier(ATOQualityTier.Balanced);

        /// <summary>EN: Custom tier parameters; persisted separately so tier switching never clobbers them.
        ///     ZH: 自定义挡位参数；单独保存，切换挡位不会覆盖它。</summary>
        public ATOQualityParams customQuality = ATOQualityParams.ForTier(ATOQualityTier.Custom);

        // ---- Atlas / 图集 ----
        public bool generateAtlas = true;

        /// <summary>EN: Experimental non-power-of-two atlas sizes (64 px steps). ZH: 实验性 NPOT 图集尺寸（64px 步进）。</summary>
        public bool experimentalNpot = false;

        public ATOMinPadding minPadding = ATOMinPadding.Px4;

        /// <summary>EN: Maximum atlas edge length. Clamped to 4096 on mobile platforms.
        ///     ZH: 图集最大边长。移动平台会钳制到 4096。</summary>
        public int maxAtlasSize = 8192;

        // ---- Output / 输出 ----
        public ATOTextureClassSettings opaqueColor = new ATOTextureClassSettings();
        public ATOTextureClassSettings transparentColor = new ATOTextureClassSettings();
        public ATOTextureClassSettings normalMap = new ATOTextureClassSettings();
        public ATOTextureClassSettings grayscale = new ATOTextureClassSettings();

        // ---- Deduplication / 去重 ----
        public bool dedupMaterials = true;
        public bool dedupTextures = true;

        public ATOTextureClassSettings ForClass(ATOTextureClass cls)
        {
            switch (cls)
            {
                case ATOTextureClass.OpaqueColor: return opaqueColor;
                case ATOTextureClass.TransparentColor: return transparentColor;
                case ATOTextureClass.NormalMap: return normalMap;
                default: return grayscale;
            }
        }

        /// <summary>EN: Effective quality parameters for this platform. ZH: 该平台的有效质量参数。</summary>
        public ATOQualityParams EffectiveQuality()
        {
            return qualityTier == ATOQualityTier.Custom ? customQuality : quality;
        }

        public ATOPlatformSettings Clone()
        {
            var c = (ATOPlatformSettings)MemberwiseClone();
            c.quality = quality.Clone();
            c.customQuality = customQuality.Clone();
            c.opaqueColor = opaqueColor.Clone();
            c.transparentColor = transparentColor.Clone();
            c.normalMap = normalMap.Clone();
            c.grayscale = grayscale.Clone();
            return c;
        }
    }

    /// <summary>
    /// EN: Root settings object stored on the avatar component.
    /// ZH: 存放在 Avatar 组件上的根设置对象。
    /// </summary>
    [Serializable]
    public class ATOSettings
    {
        /// <summary>EN: Default (all-platform) parameters. ZH: 全平台通用参数。</summary>
        public ATOPlatformSettings common = new ATOPlatformSettings();

        /// <summary>EN: Per-platform overrides. ZH: 各平台覆盖设置。</summary>
        public List<ATOPlatformSettings> platformOverrides = new List<ATOPlatformSettings>
        {
            new ATOPlatformSettings { platform = ATOPlatform.PC, enabled = false },
            new ATOPlatformSettings { platform = ATOPlatform.Android, enabled = false, maxAtlasSize = 4096 },
            new ATOPlatformSettings { platform = ATOPlatform.iOS, enabled = false, maxAtlasSize = 4096 },
        };

        /// <summary>
        /// EN: Whitelist. Any object type is accepted (mesh, renderer, material, texture, animation clip,
        ///     GameObject, ...). Every texture reachable from a whitelisted object skips *all* optimisation.
        /// ZH: 白名单。接受任意对象类型（网格、渲染器、材质、贴图、动画、GameObject 等）。
        ///     白名单对象所引用的全部贴图都会跳过所有优化。
        /// </summary>
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        // ---- Diagnostics / 调试 ----

        /// <summary>EN: Emit verbose [ATO] logs to the Unity console. ZH: 向 Unity 控制台输出详细的 [ATO] 日志。</summary>
        public bool verboseLogging = false;

        /// <summary>EN: Also dump per-island metric traces (very noisy). ZH: 同时输出逐岛指标日志（非常冗长）。</summary>
        public bool traceIslandMetrics = false;

        // ---- Localization / 本地化 ----
        public ATOLanguageMode languageMode = ATOLanguageMode.Auto;
        public string explicitLanguage = "en";

        public ATOSettings Clone()
        {
            var c = new ATOSettings
            {
                common = common.Clone(),
                platformOverrides = new List<ATOPlatformSettings>(),
                whitelist = new List<UnityEngine.Object>(whitelist),
                verboseLogging = verboseLogging,
                traceIslandMetrics = traceIslandMetrics,
                languageMode = languageMode,
                explicitLanguage = explicitLanguage,
            };
            foreach (var p in platformOverrides) c.platformOverrides.Add(p.Clone());
            return c;
        }

        /// <summary>
        /// EN: Resolve the effective settings for a build platform (override if enabled, otherwise common).
        /// ZH: 解析某个构建平台的有效设置（覆盖启用时用覆盖，否则用通用设置）。
        /// </summary>
        public ATOPlatformSettings Resolve(ATOPlatform platform)
        {
            foreach (var p in platformOverrides)
            {
                if (p.platform == platform && p.enabled) return p;
            }
            return common;
        }
    }
}
