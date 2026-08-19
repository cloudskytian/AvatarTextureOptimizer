// Avatar Texture Optimizer - runtime component & settings model.
// 运行时组件与设置数据模型（仅承载配置，处理全部发生在 Editor/NDMF 构建期）。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>Quality tier presets. / 质量挡位预设。</summary>
    public enum AtoQualityTier
    {
        Lossless = 0,   // target quality == 1, skip island scaling entirely / 目标质量1，跳过缩放
        High = 1,       // visually lossless-ish / 视觉近无损
        Balanced = 2,   // default / 默认
        Compact = 3,    // aggressive / 激进压缩
        Custom = 4      // user-defined, never overwritten by tier switching / 自定义，不被挡位切换覆盖
    }

    /// <summary>Pixel density steps (px per meter). / 像素密度挡位（px/m）。</summary>
    public enum AtoDensityStep { D512 = 512, D1024 = 1024, D2048 = 2048, D4096 = 4096, D8192 = 8192 }

    /// <summary>Minimum island padding steps. / 最小岛间距挡位。</summary>
    public enum AtoPaddingStep { P4 = 4, P8 = 8, P16 = 16, P32 = 32, P64 = 64 }

    /// <summary>Build target platform kind for overrides. / 平台覆盖类别。</summary>
    public enum AtoPlatform { PC = 0, Android = 1, iOS = 2 }

    /// <summary>
    /// Safe compression format choices, filtered per-platform at bake time.
    /// 安全压缩格式枚举，构建时按平台过滤并做兜底。
    /// </summary>
    public enum AtoOpaqueFormat { Auto = 0, BC7 = 1, DXT1 = 2, ASTC_4x4 = 10, ASTC_6x6 = 11, ASTC_8x8 = 12 }
    public enum AtoTransparentFormat { Auto = 0, BC7 = 1, DXT5 = 2, ASTC_4x4 = 10, ASTC_6x6 = 11, ASTC_8x8 = 12 }
    public enum AtoNormalFormat { Auto = 0, BC7 = 1, BC5 = 2, DXT5 = 3, ASTC_4x4 = 10, ASTC_6x6 = 11 }
    public enum AtoGrayFormat { Auto = 0, BC4 = 1, BC7 = 2, DXT1 = 3, ASTC_4x4 = 10, ASTC_6x6 = 11, ASTC_8x8 = 12 }

    /// <summary>
    /// Perceptual quality thresholds evaluated by the target-quality algorithm.
    /// All metrics must pass ("worst threshold wins"). 1-tier means near lossless.
    /// 目标质量算法阈值集；全部达标才算通过。
    /// </summary>
    [Serializable]
    public class AtoQualityParams
    {
        [Range(0f, 1f)] public float minMsSsim = 0.98f;          // MS-SSIM lower bound / 下限
        [Range(0f, 20f)] public float maxDeltaE00P95 = 1.8f;     // CIEDE2000 p95 upper bound
        [Range(0f, 1f)] public float minAlphaCutoutIoU = 0.995f; // cutout contour IoU lower bound
        [Range(0f, 1f)] public float maxAlphaBlendRmse = 0.010f; // blend alpha linear RMSE upper bound
        [Range(0f, 45f)] public float maxNormalAngleP95Deg = 3.0f; // normal angular error p95 (deg)
        [Range(0f, 1f)] public float maxGrayRmse = 0.010f;       // per-used-channel linear RMSE upper bound

        public AtoQualityParams Clone() => (AtoQualityParams)MemberwiseClone();

        /// <summary>True when this tier means "target quality == 1". / 是否近无损（跳过缩放）。</summary>
        public bool IsLossless =>
            minMsSsim >= 1f && maxDeltaE00P95 <= 0f && minAlphaCutoutIoU >= 1f &&
            maxAlphaBlendRmse <= 0f && maxNormalAngleP95Deg <= 0f && maxGrayRmse <= 0f;

        /// <summary>
        /// Research-informed presets (MS-SSIM/CIEDE2000 JND literature: dE00~1.0 barely
        /// perceptible, ~2.3 JND under ideal viewing; MS-SSIM >=0.98 commonly "visually lossless").
        /// 依据学术/业内经验的预设。
        /// </summary>
        public static AtoQualityParams ForTier(AtoQualityTier tier)
        {
            switch (tier)
            {
                case AtoQualityTier.Lossless:
                    return new AtoQualityParams { minMsSsim = 1f, maxDeltaE00P95 = 0f, minAlphaCutoutIoU = 1f, maxAlphaBlendRmse = 0f, maxNormalAngleP95Deg = 0f, maxGrayRmse = 0f };
                case AtoQualityTier.High:
                    return new AtoQualityParams { minMsSsim = 0.99f, maxDeltaE00P95 = 1.0f, minAlphaCutoutIoU = 0.998f, maxAlphaBlendRmse = 0.006f, maxNormalAngleP95Deg = 2.0f, maxGrayRmse = 0.006f };
                case AtoQualityTier.Balanced:
                    return new AtoQualityParams { minMsSsim = 0.98f, maxDeltaE00P95 = 1.8f, minAlphaCutoutIoU = 0.995f, maxAlphaBlendRmse = 0.010f, maxNormalAngleP95Deg = 3.0f, maxGrayRmse = 0.010f };
                case AtoQualityTier.Compact:
                    return new AtoQualityParams { minMsSsim = 0.95f, maxDeltaE00P95 = 3.0f, minAlphaCutoutIoU = 0.99f, maxAlphaBlendRmse = 0.020f, maxNormalAngleP95Deg = 5.0f, maxGrayRmse = 0.020f };
                default: // Custom defaults to near-lossless (=1) per spec / 自定义默认全1
                    return new AtoQualityParams { minMsSsim = 1f, maxDeltaE00P95 = 0f, minAlphaCutoutIoU = 1f, maxAlphaBlendRmse = 0f, maxNormalAngleP95Deg = 0f, maxGrayRmse = 0f };
            }
        }
    }

    /// <summary>Per-platform overridable parameters. / 平台覆盖参数（参考 Unity platform override）。</summary>
    [Serializable]
    public class AtoPlatformOverride
    {
        public bool overrideEnabled = false;

        public AtoOpaqueFormat opaqueFormat = AtoOpaqueFormat.Auto;
        public AtoTransparentFormat transparentFormat = AtoTransparentFormat.Auto;
        public AtoNormalFormat normalFormat = AtoNormalFormat.Auto;
        public AtoGrayFormat grayFormat = AtoGrayFormat.Auto;

        // Mip streaming toggles per texture category. Mipmap<->MipStreaming are bound (VRChat rule).
        // 分类 Mip 开关；开 Mipmap 必开 MipStreaming，二者绑定，因此只有一个开关。
        public bool mipOpaque = true;
        public bool mipTransparent = true;
        public bool mipNormal = true;
        public bool mipGray = true;

        public AtoDensityStep minDensity = AtoDensityStep.D2048;
        public AtoDensityStep maxDensity = AtoDensityStep.D4096;
    }

    /// <summary>
    /// The one-per-avatar optimizer component. Must live on the object holding VRCAvatarDescriptor.
    /// 每 Avatar 唯一的优化组件，必须挂在含 VRCAvatarDescriptor 的对象上。
    /// </summary>
    [AddComponentMenu("Avatar Texture Optimizer/ATO Avatar Texture Optimizer")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/fosanet/AvatarTextureOptimizer")]
    public class AvatarTextureOptimizer : MonoBehaviour
#if ATO_VRCSDK
        , VRC.SDKBase.IEditorOnly
#endif
    {
        // ---- Quality / 质量 ----
        public AtoQualityTier qualityTier = AtoQualityTier.Balanced;
        public AtoQualityParams customQuality = AtoQualityParams.ForTier(AtoQualityTier.Custom);

        // ---- Atlas / 图集 ----
        public bool generateAtlas = true;                 // off: scale whole textures only / 关闭则仅整图缩放
        public bool experimentalNpot = false;             // NPOT candidate pool (64 step) / 实验性NPOT
        public AtoPaddingStep minPadding = AtoPaddingStep.P4;

        // ---- Density (px per meter) / 像素密度 ----
        public AtoDensityStep minDensity = AtoDensityStep.D2048;
        public AtoDensityStep maxDensity = AtoDensityStep.D4096;

        // ---- Dedup switches / 去重开关 ----
        public bool dedupMaterials = true;
        public bool dedupTextures = true;

        // ---- Whitelist: any object type (mesh/material/texture/animation/renderer/GameObject...).
        // 白名单：不限对象类型，其引用到的贴图跳过全部优化。 ----
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        // ---- Platform overrides / 平台覆盖 ----
        public AtoPlatformOverride pcOverride = new AtoPlatformOverride();
        public AtoPlatformOverride androidOverride = new AtoPlatformOverride();
        public AtoPlatformOverride iosOverride = new AtoPlatformOverride();

        // ---- Misc / 其他 ----
        public string languageOverride = "";              // "" = Auto (follow NDMF) / 空=跟随NDMF
        public bool verboseLog = true;                    // [ATO] debug logging switch / 调试日志开关
        public bool keepTempAssetsOnCancel = true;        // spec: cancel keeps temp assets on disk / 取消保留临时资产

        /// <summary>Resolve effective quality params for the current tier. / 取当前挡位的有效质量参数。</summary>
        public AtoQualityParams EffectiveQuality =>
            qualityTier == AtoQualityTier.Custom ? customQuality : AtoQualityParams.ForTier(qualityTier);
    }
}
