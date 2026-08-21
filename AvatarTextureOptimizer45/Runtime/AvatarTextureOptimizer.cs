using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato
{
    // ============================================================================
    // AvatarTextureOptimizer
    // 运行时配置组件 / Runtime configuration component.
    //
    // 挂载规则 / Mounting rules:
    //  * 整个 Avatar(含子级) 只允许挂载一个本组件 / Only ONE instance allowed on the avatar (incl. children).
    //  * 挂载的 GameObject 上必须存在 VRCAvatarDescriptor / The host GameObject must have a VRCAvatarDescriptor.
    //  * 违反规则时 NDMF 烘焙会中止并报错 / Violations abort the NDMF build with an error.
    //
    // 所有字段均为用户可配置项, 工具处于开发阶段, 字段可随意调整 / All fields are user-facing;
    // the tool is in development, fields may change freely.
    // ============================================================================

    /// <summary>
    /// 质量挡位 / Quality presets. 阈值依据学术/业内研究成果设定, 见 README 与 CLAUDE.md.
    /// Thresholds are based on published research; see README and CLAUDE.md.
    /// </summary>
    public enum ATOQualityPreset
    {
        /// <summary>近无损: 跳过缩放, 原样拷贝 / Near-lossless: skip resizing, copy as-is.</summary>
        Lossless = 0,
        /// <summary>高 / High (default)</summary>
        High = 1,
        /// <summary>中 / Medium</summary>
        Medium = 2,
        /// <summary>低 / Low (aggressive)</summary>
        Low = 3,
        /// <summary>自定义: 参数由用户设定, 默认全部为最严苛(近无损), 不受其他挡位覆盖 / Custom: user-defined, defaults are strictest (near-lossless).</summary>
        Custom = 4
    }

    /// <summary>
    /// 贴图压缩格式枚举(安全子集) / Safe enumeration of texture compression formats.
    /// 构建时会按平台能力与 NPOT 开关过滤非法项 / Invalid entries are filtered at build time
    /// based on platform capability and the NPOT toggle.
    /// </summary>
    public enum ATOCompressionFormat
    {
        /// <summary>自动(平台最优解) / Automatic (platform best choice) — default.</summary>
        Auto = 0,
        Uncompressed = 1,
        R8 = 2,
        BC1 = 3,
        BC7 = 4,
        BC5 = 5,
        ETC2_RGB4 = 6,
        ETC2_RGBA8 = 7,
        EAC_R = 8,
        EAC_RG = 9,
        ASTC_4x4 = 10,
        ASTC_6x6 = 11,
        ASTC_8x8 = 12,
        ASTC_10x10 = 13,
        PVRTC_RGB4 = 14,
        PVRTC_RGBA4 = 15
    }

    /// <summary>
    /// 自定义质量挡位参数 / Custom quality preset parameters.
    /// 相似度指标(SSIM/IoU)越接近 1 越严格; 误差指标(ΔE/RMSE/角度)越接近 0 越严格.
    /// Similarity metrics closer to 1 are stricter; error metrics closer to 0 are stricter.
    /// 默认全部取最严苛值 => 等价近无损 / Defaults are the strictest => equivalent to near-lossless.
    /// </summary>
    [Serializable]
    public class ATOQualityParameters
    {
        [Tooltip("MS-SSIM 下限 (SSIM for small islands) / Minimum MS-SSIM")]
        [Range(0.5f, 1f)] public float msSsim = 1f;

        [Tooltip("CIEDE2000 ΔE 平均值上限 / Maximum mean ΔE2000")]
        [Min(0f)] public float deltaE2000 = 0f;

        [Tooltip("Cutout 剪裁轮廓 IoU 下限 / Minimum clipped-outline IoU for cutout")]
        [Range(0f, 1f)] public float alphaIoU = 1f;

        [Tooltip("Blend 线性 alpha RMSE 上限 / Maximum linear alpha RMSE for blend")]
        [Min(0f)] public float alphaRmse = 0f;

        [Tooltip("法线贴图平均角度误差上限(度) / Max mean normal angle error (degrees)")]
        [Min(0f)] public float normalAngleMean = 0f;

        [Tooltip("法线贴图 p95 角度误差上限(度) / Max p95 normal angle error (degrees)")]
        [Min(0f)] public float normalAngleP95 = 0f;

        [Tooltip("灰度贴图线性 RMSE 上限 / Maximum linear-space RMSE for grayscale")]
        [Min(0f)] public float grayscaleRmse = 0f;

        public ATOQualityParameters Clone() => (ATOQualityParameters)MemberwiseClone();
    }

    /// <summary>
    /// 平台 override 设置 / Per-platform override settings.
    /// 勾选 override 后, 该平台的对应参数取代全局参数 / When overrides are enabled,
    /// they replace the global parameters for that platform.
    /// </summary>
    [Serializable]
    public class ATOPlatformSettings
    {
        public bool overrideEnabled;

        [Tooltip("覆盖质量挡位 / Override quality preset")]
        public bool overrideQuality;
        public ATOQualityPreset qualityPreset = ATOQualityPreset.High;

        [Tooltip("覆盖像素密度 / Override texel density (px/m)")]
        public bool overrideDensity;
        [Min(0)] public float minTexelDensity = 2048f;
        [Min(0)] public float maxTexelDensity = 4096f;

        [Tooltip("覆盖图集生成开关 / Override atlas generation")]
        public bool overrideAtlas;
        public bool enableAtlas = true;

        [Tooltip("覆盖最小 padding / Override minimum padding")]
        public bool overridePadding;
        public int minPadding = 4;

        [Tooltip("覆盖图集最大边长 / Override max atlas size (mobile defaults 4096)")]
        public bool overrideMaxAtlasSize;
        public int maxAtlasSize = 4096;

        [Tooltip("覆盖压缩格式 / Override compression formats (per category)")]
        public bool overrideCompression;
        public ATOCompressionFormat opaqueFormat = ATOCompressionFormat.Auto;
        public ATOCompressionFormat transparentFormat = ATOCompressionFormat.Auto;
        public ATOCompressionFormat normalFormat = ATOCompressionFormat.Auto;
        public ATOCompressionFormat grayscaleFormat = ATOCompressionFormat.Auto;

        [Tooltip("覆盖 Mipmap/MipStreaming / Override mipmap+mip-streaming (bound together)")]
        public bool overrideMipmaps;
        public bool enableMipmaps = true;
    }

    /// <summary>
    /// AvatarTextureOptimizer 组件 / The main component.
    /// </summary>
    [AddComponentMenu("Fosa/Avatar Texture Optimizer (ATO)")]
    [DisallowMultipleComponent]
    public sealed class AvatarTextureOptimizer : MonoBehaviour
    {
        // ------------------------------------------------------------------
        // 图集 / Atlas
        // ------------------------------------------------------------------
        [Header("Atlas / 图集")]
        [Tooltip("生成图集: 剔除未使用UV、重排UV、合并图集; 关闭后仅整图缩放 / Generate atlases: crop unused UVs, repack UVs; when off, only whole-texture scaling is applied.")]
        public bool enableAtlas = true;

        [Tooltip("岛间最小 padding (px), 实际取 max(此值, 图集最大边长/128 向上取整) / Minimum island padding (px); effective padding is max(this, ceil(atlas max side / 128)).")]
        public int minPadding = 4;

        [Tooltip("实验性 NPOT 图集边长(64 步进); 已验证支持 MipStreaming/Crunch / Experimental NPOT atlas sizes (64px steps); verified to support MipStreaming/Crunch.")]
        public bool enableNPOT = false;

        // ------------------------------------------------------------------
        // 质量 / Quality
        // ------------------------------------------------------------------
        [Header("Quality / 质量")]
        [Tooltip("目标质量挡位 / Target quality preset")]
        public ATOQualityPreset qualityPreset = ATOQualityPreset.High;

        [Tooltip("自定义挡位参数(仅在 qualityPreset=Custom 时生效) / Custom parameters (only used when qualityPreset = Custom)")]
        public ATOQualityParameters customQuality = new ATOQualityParameters();

        // ------------------------------------------------------------------
        // 像素密度 / Texel density (px/m)
        // ------------------------------------------------------------------
        [Header("Texel Density / 像素密度")]
        [Tooltip("最小像素密度 px/m (防止发糊) / Minimum texel density in px per meter (anti-blur).")]
        [Min(0)] public float minTexelDensity = 2048f;

        [Tooltip("最大像素密度 px/m (防止浪费) / Maximum texel density in px per meter (anti-waste).")]
        [Min(0)] public float maxTexelDensity = 4096f;

        // ------------------------------------------------------------------
        // Mipmap 与 MipStreaming (二者绑定) / Mipmap & MipStreaming (bound together)
        // ------------------------------------------------------------------
        [Header("Mipmap / 多级渐远")]
        [Tooltip("VRChat 要求开启 Mipmap 时必须开启 MipStreaming, 因此二者绑定为一个开关 / VRChat requires MipStreaming when mipmaps are enabled, so both are controlled by this single toggle.")]
        public bool enableMipmaps = true;

        // ------------------------------------------------------------------
        // 压缩格式(按贴图类别) / Compression formats (per texture category)
        // ------------------------------------------------------------------
        [Header("Compression / 压缩格式")]
        [Tooltip("不透明贴图/图集格式 / Format for opaque textures/atlases")]
        public ATOCompressionFormat opaqueFormat = ATOCompressionFormat.Auto;
        [Tooltip("透明贴图/图集格式 / Format for transparent textures/atlases")]
        public ATOCompressionFormat transparentFormat = ATOCompressionFormat.Auto;
        [Tooltip("法线贴图/图集格式 / Format for normal map textures/atlases")]
        public ATOCompressionFormat normalFormat = ATOCompressionFormat.Auto;
        [Tooltip("灰度贴图/图集格式 / Format for grayscale textures/atlases")]
        public ATOCompressionFormat grayscaleFormat = ATOCompressionFormat.Auto;

        // ------------------------------------------------------------------
        // 平台 override / Platform overrides
        // ------------------------------------------------------------------
        [Header("Platform Overrides / 平台覆盖")]
        public ATOPlatformSettings windows = new ATOPlatformSettings { maxAtlasSize = 8192 };
        public ATOPlatformSettings android = new ATOPlatformSettings { maxAtlasSize = 4096 };
        public ATOPlatformSettings ios = new ATOPlatformSettings { maxAtlasSize = 4096 };

        // ------------------------------------------------------------------
        // 白名单 / Whitelist
        // ------------------------------------------------------------------
        [Header("Whitelist / 白名单")]
        [Tooltip("白名单对象(不限类型: 网格/材质/贴图/动画/GameObject); 其引用的全部贴图跳过所有优化 / Whitelisted objects (any type: mesh/material/texture/animation/GameObject); all textures referenced by them skip ALL optimization.")]
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        // ------------------------------------------------------------------
        // 去重与合并 / Dedup & merging
        // ------------------------------------------------------------------
        [Header("Dedup / 去重合并")]
        [Tooltip("优化后对内容与参数完全相同的材质去重并更新引用 / Dedup materials that are fully identical after optimization.")]
        public bool dedupMaterials = true;
        [Tooltip("优化后对内容与导入设置完全相同的贴图/图集去重并更新引用 / Dedup textures/atlases that are fully identical after optimization.")]
        public bool dedupTextures = true;
        [Tooltip("合并同一网格内可判定为相同的不透明材质槽并更新动画引用 / Merge identical opaque material slots on the same mesh and remap animation references.")]
        public bool mergeOpaqueSlots = true;

        // ------------------------------------------------------------------
        // 高级 / Advanced
        // ------------------------------------------------------------------
        [Header("Advanced / 高级")]
        [Tooltip("输出详细 [ATO] 调试日志(含每步耗时、图集来源、利用率等) / Verbose [ATO] debug logs (timings, atlas sources, utilization, ...).")]
        public bool debugLogging = false;

        [Tooltip("界面语言: Auto 读取 NDMF 当前语言, 缺翻译回退英文 / UI language: Auto follows NDMF's language, falling back to English.")]
        public string language = "Auto";

        private void Reset()
        {
            // 默认值 / Defaults
            enableAtlas = true;
            minPadding = 4;
            enableNPOT = false;
            qualityPreset = ATOQualityPreset.High;
            customQuality = new ATOQualityParameters();
            minTexelDensity = 2048f;
            maxTexelDensity = 4096f;
            enableMipmaps = true;
            opaqueFormat = transparentFormat = normalFormat = grayscaleFormat = ATOCompressionFormat.Auto;
            windows = new ATOPlatformSettings { maxAtlasSize = 8192 };
            android = new ATOPlatformSettings { maxAtlasSize = 4096 };
            ios = new ATOPlatformSettings { maxAtlasSize = 4096 };
            whitelist = new List<UnityEngine.Object>();
            dedupMaterials = dedupTextures = mergeOpaqueSlots = true;
            debugLogging = false;
            language = "Auto";
        }

        private void OnValidate()
        {
            // padding 只允许 4/8/16/32/64 / padding is limited to 4/8/16/32/64
            minPadding = ClampPadding(minPadding);
            if (windows != null) windows.minPadding = ClampPadding(windows.minPadding);
            if (android != null) android.minPadding = ClampPadding(android.minPadding);
            if (ios != null) ios.minPadding = ClampPadding(ios.minPadding);
        }

        internal static int ClampPadding(int v)
        {
            if (v <= 4) return 4;
            if (v <= 8) return 8;
            if (v <= 16) return 16;
            if (v <= 32) return 32;
            return 64;
        }
    }
}
