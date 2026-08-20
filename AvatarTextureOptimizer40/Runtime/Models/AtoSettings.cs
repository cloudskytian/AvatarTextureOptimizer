using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace Fosa.Ato.Runtime
{
    // ------------------------------------------------------------------
    // Quality preset / 质量挡位
    // ------------------------------------------------------------------
    /// <summary>
    /// Built-in quality presets. "Custom" is fully user-controlled and never overwritten by
    /// other presets; its parameters default to 1.0 (near-lossless).
    /// 内置质量挡位。Custom 完全由用户控制，不会被其他挡位覆盖，参数默认全部为 1（近无损）。
    /// </summary>
    public enum QualityPreset
    {
        Low = 0,
        Medium = 1,
        High = 2,
        VeryHigh = 3,
        NearLossless = 4,
        Custom = 100,
    }

    /// <summary>Target build platform / 目标平台。</summary>
    public enum AtoPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2,
    }

    /// <summary>How a texture's alpha is used / 贴图透明模式。</summary>
    public enum AlphaMode
    {
        Auto = 0,
        Opaque = 1,
        AlphaBlend = 2,
        AlphaCutout = 3,
    }

    /// <summary>Logical texture category / 贴图类别。</summary>
    public enum TextureKind
    {
        Color = 0,       // base / main color 主色
        Normal = 1,      // tangent-space normal 法线
        Mask = 2,        // single/grayscale data mask 蒙版/灰度数据
        Emission = 3,    // emissive 自发光
        Data = 4,        // other data (metallic/smoothness etc.) 其他数据
    }

    /// <summary>Minimum atlas padding options / 最小图集 padding 挡位。</summary>
    public enum PaddingMode
    {
        P4 = 4,
        P8 = 8,
        P16 = 16,
        P32 = 32,
        P64 = 64,
    }

    // ------------------------------------------------------------------
    // Per-texture-class parameters (shared by global + platform override)
    // 每类贴图的参数（通用与平台 override 共用）
    // ------------------------------------------------------------------
    [Serializable]
    public class TextureClassSettings
    {
        // Quality / 质量
        [Tooltip("MS-SSIM threshold (0..1). Higher = better quality / MS-SSIM 阈值，越高越好")]
        [Range(0.80f, 1.0f)] public float MsSsim = 0.985f;
        [Tooltip("CIEDE2000 average delta-E threshold (lower = better) / CIEDE2000 平均色差阈值，越低越好")]
        [Range(0.2f, 10f)] public float DeltaE = 1.5f;
        [Tooltip("Cutout contour IoU threshold (0..1) / Cutout 轮廓 IoU 阈值")]
        [Range(0.9f, 1.0f)] public float AlphaCutoutIou = 0.99f;
        [Tooltip("Blend alpha linear RMSE threshold (0..1) / Blend alpha 线性 RMSE 阈值")]
        [Range(0.002f, 0.1f)] public float AlphaBlendRmse = 0.01f;
        [Tooltip("Normal map angular error (degrees) / 法线角度误差（度）")]
        [Range(0.5f, 15f)] public float NormalAngleDeg = 2.0f;
        [Tooltip("Normal map p95 angular error (degrees) / 法线 p95 角度误差（度）")]
        [Range(1f, 25f)] public float NormalP95Deg = 4.0f;
        [Tooltip("Grayscale/data per-channel linear RMSE (0..1) / 灰度/数据逐通道线性 RMSE")]
        [Range(0.005f, 0.1f)] public float DataRmse = 0.02f;

        // Mipmap + MipStreaming are bound together (VRChat requirement).
        // Mipmap 与 MipStreaming 绑定（VRChat 要求）
        [Tooltip("Enable mipmaps AND Mip Streaming together / 同时开启 Mipmap 与 Mip Streaming")]
        public bool MipmapAndStreaming = true;

        // Compression / 压缩格式（运行时只存字符串，Editor 端再做安全枚举映射）
        [Tooltip("Compression format token (resolved per platform at build time) / 压缩格式标识")]
        public string CompressionFormat = "Auto";
        [Tooltip("Compression quality (0=Fast,1=Normal,2=Best) / 压缩质量")]
        [Range(0, 2)] public int CompressionQuality = 2;
        [Tooltip("Enable Crunch compression where supported / 在支持的情况下启用 Crunch")]
        public bool Crunch = false;

        public TextureClassSettings Clone() => (TextureClassSettings)MemberwiseClone();
    }

    // ------------------------------------------------------------------
    // Platform override / 平台覆盖
    // ------------------------------------------------------------------
    [Serializable]
    public class PlatformOverride
    {
        public bool Enabled;
        public AtoPlatform Platform;

        [Header("Atlas limits / 图集上限")]
        public int MaxAtlasSize = 4096;
        public bool ExperimentalNpot = false;

        [Header("Texture classes / 各类贴图参数")]
        public TextureClassSettings Opaque = new();
        public TextureClassSettings Transparent = new();
        public TextureClassSettings Normal = new();
        public TextureClassSettings Grayscale = new();
    }

    // ------------------------------------------------------------------
    // Global settings / 全局设置
    // ------------------------------------------------------------------
    [Serializable]
    public class AtoSettings
    {
        // ---- Toggles / 总开关 ----
        [Tooltip("Master enable / 总开关")]
        public bool Enabled = true;

        [Tooltip("Generate atlases. When off: do not cull/repack UVs, only scale whole textures / " +
                 "生成图集。关闭时不剔除/重排 UV，仅整图缩放")]
        public bool GenerateAtlas = true;

        [Tooltip("Deduplicate identical materials after optimization / 优化后对相同材质去重")]
        public bool DeduplicateMaterials = true;

        [Tooltip("Deduplicate identical textures/atlases after optimization / 优化后对相同贴图/图集去重")]
        public bool DeduplicateTextures = true;

        [Tooltip("Merge identical opaque material slots and update references / " +
                 "合并相同的不透明材质槽并更新引用")]
        public bool MergeOpaqueSlots = true;

        [Tooltip("Enable Mip Streaming by default for non-whitelisted textures / " +
                 "非白名单贴图默认开启 Mip Streaming")]
        public bool DefaultMipStreaming = true;

        // ---- Quality / 质量 ----
        [Tooltip("Quality preset / 质量挡位")]
        public QualityPreset Preset = QualityPreset.High;

        [Tooltip("Minimum pixel density (px per meter) / 最小像素密度 px/m")]
        public int MinPixelDensity = 2048;
        [Tooltip("Maximum pixel density (px per meter) / 最大像素密度 px/m")]
        public int MaxPixelDensity = 4096;

        [Tooltip("Per-texture-class parameters (global / advanced) / 各类贴图参数（全局/高级）")]
        public TextureClassSettings Opaque = new();
        public TextureClassSettings Transparent = new() { Crunch = false };
        public TextureClassSettings Normal = new() { MsSsim = 0.99f, DeltaE = 1.0f };
        public TextureClassSettings Grayscale = new() { MsSsim = 0.99f, DeltaE = 0.8f };

        // ---- Atlas / 图集 ----
        [Tooltip("Max atlas size for PC (non-overridden) / PC 最大图集边长")]
        public int MaxAtlasSizePC = 8192;
        [Tooltip("Maximum atlas size for mobile / 移动端最大图集边长")]
        public int MaxAtlasSizeMobile = 4096;
        [Tooltip("Experimental NPOT atlas sizes / 实验性 NPOT 图集尺寸")]
        public bool ExperimentalNpot = false;
        [Tooltip("Minimum inter-island padding / 最小岛间 padding")]
        public PaddingMode MinPadding = PaddingMode.P4;

        // ---- Whitelist (object references, serialised via component) / 白名单 ----
        // Stored on the component directly (UnityEngine.Object refs), not here.

        // ---- Platform overrides / 平台覆盖 ----
        public PlatformOverride OverridePC = new() { Platform = AtoPlatform.PC, MaxAtlasSize = 8192 };
        public PlatformOverride OverrideAndroid = new() { Platform = AtoPlatform.Android, MaxAtlasSize = 4096 };
        public PlatformOverride OverrideIOS = new() { Platform = AtoPlatform.iOS, MaxAtlasSize = 4096 };

        // ---- Debug / 调试 ----
        [Tooltip("Verbose [ATO] logs (timings, island counts, atlas utilization, ...) / " +
                 "详细日志（耗时、岛数量、图集利用率等）")]
        public bool VerboseLogging = false;

        // ---- Preset application / 应用挡位 ----
        /// <summary>
        /// Apply a built-in preset's parameters WITHOUT touching Custom. Returns true if applied.
        /// 应用内置挡位参数，不影响 Custom。
        /// </summary>
        public bool ApplyPreset(QualityPreset p)
        {
            Preset = p;
            switch (p)
            {
                case QualityPreset.Low:
                    SetAll(MsSsim: 0.95f, deltaE: 3.0f, cutout: 0.97f, blend: 0.03f, nAng: 6f, nP95: 12f, data: 0.05f, densityMin: 512, densityMax: 2048);
                    return true;
                case QualityPreset.Medium:
                    SetAll(0.975f, 2.0f, 0.985f, 0.018f, 3.5f, 7f, 0.03f, 1024, 4096);
                    return true;
                case QualityPreset.High:
                    SetAll(0.985f, 1.5f, 0.99f, 0.01f, 2.0f, 4f, 0.02f, 2048, 4096);
                    return true;
                case QualityPreset.VeryHigh:
                    SetAll(0.992f, 0.8f, 0.995f, 0.006f, 1.2f, 2.5f, 0.012f, 4096, 8192);
                    return true;
                case QualityPreset.NearLossless:
                    SetAll(0.998f, 0.3f, 0.999f, 0.0025f, 0.6f, 1.2f, 0.006f, 4096, 8192);
                    return true;
                case QualityPreset.Custom:
                    // Never overwrite custom / 不覆盖自定义
                    return true;
            }
            return false;
        }

        private void SetAll(float MsSsim, float deltaE, float cutout, float blend, float nAng, float nP95, float data, int densityMin, int densityMax)
        {
            MinPixelDensity = densityMin;
            MaxPixelDensity = densityMax;
            foreach (var c in new[] { Opaque, Transparent, Normal, Grayscale })
            {
                c.MsSsim = MsSsim; c.DeltaE = deltaE;
                c.AlphaCutoutIou = cutout; c.AlphaBlendRmse = blend;
                c.NormalAngleDeg = nAng; c.NormalP95Deg = nP95; c.DataRmse = data;
            }
            // Tighten normal slightly relative to color defaults / 法线相对主色更严格一点
            Normal.MsSsim = Mathf.Max(Normal.MsSsim, 0.99f);
        }

        public TextureClassSettings GetClass(TextureKind kind, bool hasAlpha)
        {
            if (kind == TextureKind.Normal) return Normal;
            if (kind == TextureKind.Mask || kind == TextureKind.Data) return Grayscale;
            if (kind == TextureKind.Emission) return Opaque;
            return hasAlpha ? Transparent : Opaque;
        }

        public PlatformOverride GetOverride(AtoPlatform platform) => platform switch
        {
            AtoPlatform.PC => OverridePC,
            AtoPlatform.Android => OverrideAndroid,
            AtoPlatform.iOS => OverrideIOS,
            _ => null,
        };
    }
}
