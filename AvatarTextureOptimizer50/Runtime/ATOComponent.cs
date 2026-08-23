// -----------------------------------------------------------------------------
// ATOComponent.cs — the single ATO component + all serialized settings.
// ATOComponent.cs — ATO 唯一组件与全部序列化设置。
//
// AvatarTextureOptimizer (ATO) — NDMF texture optimizer for VRChat avatars.
// Dual-language comments: EN first, ZH second.
// 双语注释：先英文，后中文。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>Quality preset levels. Values are based on perceptual research, see ATOPresets.
    /// 质量挡位。取值依据感知研究，见 ATOPresets。</summary>
    public enum ATOQualityPreset
    {
        /// <summary>Near lossless: MS-SSIM target 1.0 → islands are copied untouched.
        /// 近无损：MS-SSIM 目标为 1 → 岛原样拷贝不缩放。</summary>
        NearLossless = 0,

        /// <summary>Default. Imperceptible difference in most content.
        /// 默认挡位。绝大多数内容下差异不可感知。</summary>
        High = 1,

        /// <summary>Noticeable only under close inspection.
        /// 仅近距离检查时可察觉。</summary>
        Medium = 2,

        /// <summary>Aggressive size reduction; slight softening possible.
        /// 激进缩小；可能出现轻微模糊。</summary>
        Aggressive = 3,

        /// <summary>User-managed values; never overwritten by other presets.
        /// 用户自定义参数；不会被其他挡位覆盖。</summary>
        Custom = 4,
    }

    /// <summary>Verbosity of [ATO] logs / [ATO] 日志级别。</summary>
    public enum ATOLogLevel
    {
        Error = 0,
        Warning = 1,
        Info = 2,
        Debug = 3,
        Trace = 4,
    }

    /// <summary>Target platform for overrides / 平台覆盖目标。</summary>
    public enum ATOPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2,
    }

    /// <summary>Texture compression format choice (safe subset; resolved per platform at build time).
    /// 贴图压缩格式安全枚举（构建时按平台解析并兜底）。</summary>
    public enum ATOFormat
    {
        /// <summary>Let ATO pick the best safe format / 由 ATO 自动选择最安全格式。</summary>
        Auto = 0,

        // ---- PC (DXT family) ----
        DXT1 = 10,
        DXT5 = 11,
        BC7 = 12,
        DXT1Crunched = 13,
        DXT5Crunched = 14,
        BC5 = 15,       // normals only / 仅法线

        // ---- Android / mobile ----
        ASTC4x4 = 20,
        ASTC5x5 = 21,
        ASTC6x6 = 22,
        ASTC8x8 = 23,
        ETC2_RGB = 24,
        ETC2_RGBA8 = 25,
        ETC2RGBA8Crunched = 26,

        // ---- iOS only, POT atlases only (excluded when NPOT checked)
        //     仅 iOS、仅 POT 图集（勾选 NPOT 时剔除） ----
        PVRTC4RGB = 30,
        PVRTC4RGBA = 31,

        /// <summary>Keep uncompressed RGBA32 / 保持未压缩 RGBA32。</summary>
        RGBA32 = 90,
    }

    /// <summary>Per-class format settings / 按贴图类别的格式设置。</summary>
    [Serializable]
    public class ATOFormatSet
    {
        // Atlases & fallback (non-whitelisted) textures, by class:
        // 图集与 fallback（非白名单）贴图，按类别：
        public ATOFormat albedoOpaque = ATOFormat.Auto;
        public ATOFormat albedoAlpha = ATOFormat.Auto;
        public ATOFormat normalMap = ATOFormat.Auto;
        public ATOFormat grayMask = ATOFormat.Auto;

        public ATOFormatSet Clone() => (ATOFormatSet)MemberwiseClone();

        /// <summary>Reset to Auto / 全部重置为 Auto。</summary>
        public void ResetToAuto()
        {
            albedoOpaque = ATOFormat.Auto;
            albedoAlpha = ATOFormat.Auto;
            normalMap = ATOFormat.Auto;
            grayMask = ATOFormat.Auto;
        }
    }

    /// <summary>
    /// Quality thresholds. All "worse is bigger" metrics; a candidate scale passes only when
    /// EVERY metric satisfies its threshold (barrel/worst-of-all rule across textures of a UV group).
    /// 质量阈值。全部按"越差越大"；候选缩放必须所有指标同时达标（UV 组内所有贴图取木桶最严）。
    /// </summary>
    [Serializable]
    public class ATOQualityParams
    {
        [Tooltip("MS-SSIM threshold (luma). 1 = lossless → island scaling skipped entirely. Short side <176px falls back to single-scale SSIM; <11px ignores this metric. | MS-SSIM 阈值（亮度）。1=无损→完全跳过岛缩放。短边<176px回退单尺度SSIM；<11px忽略本指标。")]
        [Range(0.5f, 1f)]
        public float msSsim = 0.98f;

        [Tooltip("Mean CIEDE2000 color difference (JND ≈ 1.0). | CIEDE2000 平均色差（JND≈1.0）。")]
        [Range(0f, 10f)]
        public float deltaE = 1.0f;

        [Tooltip("Cutout alpha: min IoU of the clipped silhouette. | Cutout 透明：clip 后轮廓 IoU 下限。")]
        [Range(0.9f, 1f)]
        public float alphaIou = 0.995f;

        [Tooltip("Blend alpha: max linear RMSE in 0..255 units. | Blend 透明：线性 RMSE 上限（0..255 单位）。")]
        [Range(0f, 32f)]
        public float alphaRmse = 2.5f;

        [Tooltip("Normal map: max mean angular error (degrees). | 法线：平均角度误差上限（度）。")]
        [Range(0f, 10f)]
        public float normalAngleMean = 1.0f;

        [Tooltip("Normal map: max p95 angular error (degrees). | 法线：p95 角度误差上限（度）。")]
        [Range(0f, 20f)]
        public float normalAngleP95 = 3.0f;

        [Tooltip("Grayscale masks: max per-channel linear RMSE (0..255 units), worst used channel. | 灰度蒙版：逐使用通道线性 RMSE 上限（0..255），取最差。")]
        [Range(0f, 32f)]
        public float grayRmse = 2.0f;

        public ATOQualityParams Clone() => (ATOQualityParams)MemberwiseClone();

        /// <summary>True when scaling must be skipped entirely (near-lossless copy mode).
        /// 当目标质量为 1（近无损）时返回 true：跳过缩放、原样拷贝。</summary>
        public bool IsLossless => msSsim >= 1f;
    }

    /// <summary>Per-platform override block (format set + atlas size cap).
    /// 平台覆盖块（格式集 + 图集尺寸上限）。</summary>
    [Serializable]
    public class ATOPlatformOverride
    {
        /// <summary>When false, shared (PC) values are used. / false 时使用通用设置。</summary>
        public bool enabled = false;

        public ATOFormatSet formats = new ATOFormatSet();

        /// <summary>Max atlas edge. Mobile defaults to 4096. / 图集最大边。移动端默认 4096。</summary>
        public int maxAtlasSize = 8192;

        public ATOPlatformOverride Clone() => new ATOPlatformOverride
        {
            enabled = enabled,
            formats = formats.Clone(),
            maxAtlasSize = maxAtlasSize,
        };
    }

    /// <summary>Per-class mipmap/streaming toggles. Mip and streaming are one switch
    /// (VRChat requires streaming whenever mips are on).
    /// 按类别的 Mip/流式开关。Mip 与流式绑定为单开关（VRChat 要求开 Mip 必开流式）。</summary>
    [Serializable]
    public class ATOMipSettings
    {
        public bool albedo = true;
        public bool normalMap = true;
        public bool grayMask = true;
    }

    /// <summary>
    /// The ATO avatar component. Exactly one per avatar, must sit on the avatar root
    /// (the object holding VRCAvatarDescriptor). Everything is driven at bake/build time
    /// via NDMF; the component itself does nothing at runtime.
    /// ATO 组件。每个 Avatar 仅允许一个，必须挂在持有 VRCAvatarDescriptor 的根对象上。
    /// 所有处理都在 NDMF 烘焙/构建时进行；组件运行时不做任何事。
    /// </summary>
    [AddComponentMenu("Avatar Texture Optimizer/ATO Avatar (VRChat)")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/fosa/avatar-texture-optimizer")]
    public class AvatarTextureOptimizer : MonoBehaviour
#if ATO_VRCSDK_AVATARS
        , VRC.SDKBase.IEditorOnly
#endif
    {
        // ------------------------------------------------------------------ //
        // Basic / 基础
        // ------------------------------------------------------------------ //

        [Tooltip("Generate atlases. Unchecked: scale whole textures instead (no UV edits). | 生成图集。不勾选：改为整图缩放（不改UV）。")]
        public bool generateAtlas = true;

        [Tooltip("Dedup identical materials after optimization (merge opaque slots when safe). | 优化后对完全相同的材质去重（安全时合并不透明材质槽）。")]
        public bool dedupMaterials = true;

        [Tooltip("Dedup identical textures/atlases (content+params) after optimization. | 优化后对内容与参数完全一致的贴图/图集去重。")]
        public bool dedupTextures = true;

        [Tooltip("Quality preset. Switching presets updates parameters; Custom is never overwritten. | 质量挡位。切换挡位更新参数；Custom 永不被覆盖。")]
        public ATOQualityPreset qualityPreset = ATOQualityPreset.High;

        [Tooltip("Quality thresholds (advanced). | 质量阈值（高级）。")]
        public ATOQualityParams quality = new ATOQualityParams();

        // ------------------------------------------------------------------ //
        // Density / pixel density clamps
        // ------------------------------------------------------------------ //

        [Tooltip("Min pixel density (px per meter of real mesh size) — avoids blur. | 最小像素密度（每米真实网格尺寸的像素数）——防止发糊。")]
        public int minPixelDensity = 2048;

        [Tooltip("Max pixel density — avoids waste. | 最大像素密度——防止浪费。")]
        public int maxPixelDensity = 4096;

        // ------------------------------------------------------------------ //
        // Atlas / packing
        // ------------------------------------------------------------------ //

        [Tooltip("Minimum island padding (px). Effective padding = max(atlasEdge/128 rounded up, this). | 最小岛间距（px）。实际取 max(图集边长/128 向上取整, 此值)。")]
        public int minPadding = 4;

        [Tooltip("EXPERIMENTAL: allow NPOT atlas sizes (64px steps). Verified to support MipStreaming & Crunch. PVRTC is excluded when checked. | 实验性：允许 NPOT 图集尺寸（64px 步进）。已验证支持流式Mip与Crunch；勾选时剔除 PVRTC。")]
        public bool npotAtlases = false;

        // ------------------------------------------------------------------ //
        // Whitelist / 白名单
        // ------------------------------------------------------------------ //

        [Tooltip("Objects whose referenced textures skip ALL optimization (any type: mesh/material/texture/animation/GameObject...). | 其引用的全部贴图跳过所有优化的对象（不限类型：网格/材质/贴图/动画/物体…）。")]
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        // ------------------------------------------------------------------ //
        // Mip streaming / 流式 Mip（与 Mip 绑定为同一开关）
        // ------------------------------------------------------------------ //

        public ATOMipSettings mips = new ATOMipSettings();

        // ------------------------------------------------------------------ //
        // Platform overrides / 平台覆盖
        // ------------------------------------------------------------------ //

        public ATOPlatformOverride pcOverride = new ATOPlatformOverride { maxAtlasSize = 8192 };
        public ATOPlatformOverride androidOverride = new ATOPlatformOverride { maxAtlasSize = 4096 };
        public ATOPlatformOverride iosOverride = new ATOPlatformOverride { maxAtlasSize = 4096 };

        // ------------------------------------------------------------------ //
        // Debug / 调试
        // ------------------------------------------------------------------ //

        [Tooltip("Log verbosity. | 日志级别。")]
        public ATOLogLevel logLevel = ATOLogLevel.Info;

        [Tooltip("Also write the full report to the Unity console (always shown in NDMF console). | 同时把完整报告输出到 Unity 控制台（NDMF 控制台始终显示）。")]
        public bool logReportToConsole = true;

        // ------------------------------------------------------------------ //
        // Localization / 本地化（"auto" follows NDMF language）
        // ------------------------------------------------------------------ //

        [Tooltip("UI language code, or 'auto'. | 界面语言代码，或 auto 跟随 NDMF。")]
        public string language = "auto";

        // ------------------------------------------------------------------ //

        /// <summary>Get the override block for a platform / 取平台覆盖块。</summary>
        public ATOPlatformOverride GetOverride(ATOPlatform platform)
        {
            switch (platform)
            {
                case ATOPlatform.Android: return androidOverride;
                case ATOPlatform.iOS: return iosOverride;
                default: return pcOverride;
            }
        }

        /// <summary>Effective max atlas size for the active platform / 当前平台生效的最大图集边长。</summary>
        public int EffectiveMaxAtlasSize(ATOPlatform platform)
        {
            var ov = GetOverride(platform);
            return ov.enabled ? Mathf.Clamp(ov.maxAtlasSize, 64, 8192) : (platform == ATOPlatform.PC ? 8192 : 4096);
        }
    }
}
