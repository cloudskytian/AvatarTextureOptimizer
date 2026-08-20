// AvatarTextureOptimizer - AvatarTextureOptimizer (component)
// EN: Mount on the object that carries VRCAvatarDescriptor. One per avatar hierarchy.
// CN: 挂在带 VRCAvatarDescriptor 的对象上。一个 Avatar 层级只允许一个。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// EN: Padding choices for atlas island spacing. / CN: 图集岛间距（padding）选项。
    /// </summary>
    public enum AtoPadding
    {
        P4 = 4,
        P8 = 8,
        P16 = 16,
        P32 = 32,
        P64 = 64
    }

    /// <summary>
    /// EN: The user-facing configuration component. All fields are editable; the tool is in active development,
    /// so no serialization compatibility promises are made between versions.
    /// CN: 用户可见配置组件。所有字段均可编辑；工具处于开发阶段，不做序列化版本兼容承诺。
    /// </summary>
    [AddComponentMenu("Avatar Texture Optimizer")]
    [DisallowMultipleComponent]
    public sealed class AvatarTextureOptimizer : MonoBehaviour
    {
        // ------------------------------------------------------------------ 基础
        [Header("General")] // 基础
        [Tooltip("Generate atlases. When off: no atlas, no unused-UV trimming, no UV repacking; textures are scaled whole and other optimizations still apply.")]
        public bool generateAtlases = true;

        [Tooltip("Quality preset. NearLossless = quality target 1 (skip UV island scaling, copy as-is).")]
        public QualityPresetEnum qualityPreset = QualityPresetEnum.High;

        [Tooltip("Custom quality parameters; defaults are all 1 (near-lossless). Never overwritten by other presets.")]
        public QualityParams customQuality = QualityParams.NearLossless;

        // ------------------------------------------------------------------ 像素密度
        [Header("Pixel Density (px per meter)")] // 像素密度（px/m）
        [Tooltip("Minimum texels per meter: islands below this density are not shrunk further (prevents blur).")]
        public int minPixelDensity = 2048;

        [Tooltip("Maximum texels per meter: islands above this density are shrunk toward it (prevents waste).")]
        public int maxPixelDensity = 4096;

        // ------------------------------------------------------------------ 图集
        [Header("Atlas")] // 图集
        [Tooltip("Distance between islands (px). Effective minimum is clamped to max(atlasSize/128, chosen) and never below 4.")]
        public AtoPadding padding = AtoPadding.P4;

        [Tooltip("Experimental NPOT atlas sizes (64px step). MipStreaming & Crunch supported; unsupported formats (e.g. PVRTC on iOS) are excluded automatically.")]
        public bool experimentalNpot;

        [Tooltip("Max atlas edge (px). Clamped to 4096 when building for mobile.")]
        public int maxAtlasSize = 8192;

        // ------------------------------------------------------------------ 贴图参数
        [Header("Texture Import")] // 贴图导入参数
        [Tooltip("Master switch binding Mipmap and MipStreaming together (VRChat requires streaming when mipmaps are on).")]
        public bool mipmaps = true;

        [Tooltip("Use GPU (RenderTexture + compute) for quality metrics. Self-tests against CPU; falls back automatically if mismatch.")]
        public bool useGpuMetrics = true;

        // ------------------------------------------------------------------ 压缩
        [Header("Compression")] // 压缩
        public CompressionSettings compression = new CompressionSettings();

        // ------------------------------------------------------------------ 优化后处理
        [Header("Post Optimization")] // 优化后处理
        [Tooltip("Deduplicate identical materials/textures (content & params) and update all references; merge material slots when safe.")]
        public bool enableDedup = true;

        [Tooltip("Merge identical material slots on the same mesh when animations never switch one of them individually.")]
        public bool enableSlotMerge = true;

        // ------------------------------------------------------------------ 白名单
        [Header("Whitelist")] // 白名单
        [Tooltip("Objects whose referenced textures are fully skipped (mesh/material/texture/animation). Same-UV partners skip atlas but still get whole-texture scaling + import optimization.")]
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        // ------------------------------------------------------------------ 平台覆盖
        [Header("Platform Overrides")] // 平台覆盖
        [Tooltip("Per-platform full overrides (like Unity platform overrides). Only applied when checked.")]
        public PlatformProfile pcOverride = PlatformProfile.CreateDefault();
        public PlatformProfile androidOverride = PlatformProfile.CreateDefault();
        public PlatformProfile iosOverride = PlatformProfile.CreateDefault();

        // ------------------------------------------------------------------ 调试
        [Header("Diagnostics")] // 调试
        [Tooltip("Detailed [ATO] logs with per-stage timings, atlas sources, island counts, sizes, utilization, savings.")]
        public bool detailedLogs = true;

        /// <summary>EN: Effective profile for a platform; falls back to component fields when the override is off.
        /// CN: 获取某平台的有效配置；未勾选覆盖时使用组件主字段。</summary>
        public PlatformProfile EffectiveProfile(AtoPlatform platform, out bool overridden)
        {
            PlatformProfile p = platform switch
            {
                AtoPlatform.PC => pcOverride,
                AtoPlatform.Android => androidOverride,
                _ => iosOverride
            };
            if (p != null && p.enabled) { overridden = true; return p; }
            overridden = false;
            var fallback = PlatformProfile.CreateDefault();
            fallback.preset = qualityPreset;
            fallback.customParams = customQuality;
            fallback.padding = (int)padding;
            fallback.experimentalNpot = experimentalNpot;
            fallback.maxAtlasSize = maxAtlasSize;
            fallback.mipmaps = mipmaps;
            fallback.useGpuMetrics = useGpuMetrics;
            fallback.minPixelDensity = minPixelDensity;
            fallback.maxPixelDensity = maxPixelDensity;
            fallback.compression = compression;
            return fallback;
        }

        // ------------------------------------------------------------------ 校验
        /// <summary>EN: Returns error message if the mounting is invalid, else null.
        /// CN: 挂载不合法时返回错误信息，否则返回 null。</summary>
        public string ValidateMounting()
        {
#if ATO_VRCSDK3_AVATARS
            if (GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() == null)
            {
                return I18n.T("error.mounting.descriptor");
            }
#endif
            var all = gameObject.scene != null && gameObject.scene.IsValid()
                ? GetComponentsInChildren<AvatarTextureOptimizer>(true)
                : new AvatarTextureOptimizer[0];
            if (all.Length > 1)
            {
                return I18n.T("error.mounting.duplicate");
            }
            return null;
        }

        /// <summary>EN: Finds the ATO component on an avatar hierarchy (for plugin discovery). / CN: 在 Avatar 层级查找 ATO 组件。</summary>
        public static AvatarTextureOptimizer FindOnAvatar(GameObject root)
        {
            return root.GetComponentInChildren<AvatarTextureOptimizer>(true);
        }
    }
}
