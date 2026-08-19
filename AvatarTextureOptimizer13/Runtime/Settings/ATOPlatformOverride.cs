// ATO — Avatar Texture Optimizer
// Platform override settings, mirroring Unity's own platform override model.
// 平台覆盖设置，参考 Unity 自身的 platform override 模型。
//
// Each platform (PC / Android / iOS) may optionally override every optimization
// parameter. When a platform's override is disabled, the avatar-level (base) settings
// are used. When enabled, that platform's own copy of the parameters takes effect.
// 每个平台（PC / Android / iOS）可选择覆盖全部优化参数。
// 未勾选覆盖时使用组件级（基础）设置；勾选后使用该平台自己的参数副本。

using System;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// Supported build platforms for override. 支持覆盖的构建平台。
    /// </summary>
    public enum ATOPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2,
    }

    /// <summary>
    /// A full copy of the optimization parameters, used as a per-platform override.
    /// 优化参数完整副本，用作单平台覆盖。
    /// </summary>
    [Serializable]
    public class ATOPlatformSettings
    {
        [HideInInspector] public ATOPlatform platform = ATOPlatform.PC;

        [Tooltip("Enable to override all optimization parameters for this platform. 勾选后覆盖该平台的全部优化参数。")]
        public bool overrideEnabled = false;

        [Tooltip("Target quality preset. 目标质量挡位。")]
        public ATOQualityPreset qualityPreset = ATOQualityPreset.Balanced;

        [Tooltip("Custom quality parameters (used when preset = Custom). 自定义质量参数（挡位为 Custom 时生效）。")]
        public ATOQualityParameters customParameters = ATOQualityParameters.Lossless();

        [Tooltip("Generate atlases. 生成图集。")]
        public bool generateAtlas = true;

        [Tooltip("Island padding in pixels. 岛间距（px）。")]
        [Range(4, 64)] public int islandPadding = 4;

        [Tooltip("Min pixel density (px/m). 最小像素密度（px/m）。")]
        public float minPixelDensity = 2048f;

        [Tooltip("Max pixel density (px/m). 最大像素密度（px/m）。")]
        public float maxPixelDensity = 4096f;

        [Tooltip("Experimental NPOT atlas sizes. 实验性 NPOT 图集尺寸。")]
        public bool npotAtlas = false;

        [Tooltip("Deduplicate materials. 材质去重。")]
        public bool dedupMaterials = true;

        [Tooltip("Deduplicate textures / atlases. 贴图 / 图集去重。")]
        public bool dedupTextures = true;

        [Tooltip("Mipmaps + MipStreaming (bound together). Mipmap 与 MipStreaming（绑定）。")]
        public bool mipmapsEnabled = true;

        [Tooltip("Compression per texture kind. 按贴图类型的压缩。")]
        public ATOCompressionSettings compression = new ATOCompressionSettings();

        /// <summary>Copy base settings into this platform's override. 将基础设置复制到该平台覆盖。</summary>
        public void CopyFromBase(AvatarTextureOptimizer baseSettings)
        {
            qualityPreset = baseSettings.qualityPreset;
            customParameters = baseSettings.customParameters;
            generateAtlas = baseSettings.generateAtlas;
            islandPadding = baseSettings.islandPadding;
            minPixelDensity = baseSettings.minPixelDensity;
            maxPixelDensity = baseSettings.maxPixelDensity;
            npotAtlas = baseSettings.npotAtlas;
            dedupMaterials = baseSettings.dedupMaterials;
            dedupTextures = baseSettings.dedupTextures;
            mipmapsEnabled = baseSettings.mipmapsEnabled;
            compression = baseSettings.compression.Clone();
        }
    }
}
