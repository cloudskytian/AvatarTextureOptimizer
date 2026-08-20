// AvatarTextureOptimizer - PlatformProfile
// EN: Per-platform full override of every optimization parameter (mirrors Unity's platform override concept).
// CN: 按平台整体覆盖所有优化参数（参考 Unity 的 platform override 概念）。
using System;

namespace net.fosa.avatar_texture_optimizer
{
    public enum AtoPlatform
    {
        PC = 0,     // Standalone / Windows
        Android = 1, // Quest
        iOS = 2
    }

    /// <summary>
    /// EN: One profile per platform. Only applied when enabled; inspector shows it only when checked.
    /// CN: 每平台一个配置档。仅在勾选后生效；Inspector 勾选对应平台才显示。
    /// </summary>
    [Serializable]
    public class PlatformProfile
    {
        public bool enabled;
        public bool overrideQuality = true;
        public QualityPresetEnum preset = QualityPresetEnum.High;
        public QualityParams customParams = QualityParams.NearLossless;

        // EN: Atlas & texture parameters (full override when enabled). / CN: 图集与贴图参数（勾选后整体覆盖）。
        public int padding = 4;
        public bool experimentalNpot;
        public int maxAtlasSize = 8192;
        public bool mipmaps = true;          // 绑定 Mipmap ⇔ MipStreaming
        public bool useGpuMetrics = true;
        public int minPixelDensity = 2048;
        public int maxPixelDensity = 4096;
        public CompressionSettings compression = new CompressionSettings();

        public static PlatformProfile CreateDefault() => new PlatformProfile();

        /// <summary>EN: Effective quality params for this profile. / CN: 该配置档的有效质量参数。</summary>
        public QualityParams EffectiveQuality()
        {
            return QualityParams.Resolve(preset, customParams);
        }
    }
}
