// AvatarTextureOptimizer 全局常量。Global constants for AvatarTextureOptimizer.
namespace Fosa.AvatarTextureOptimizer
{
    public static class ATOConstants
    {
        // 日志前缀。Log prefix.
        public const string LogPrefix = "[ATO]";

        // 工具版本。Tool version.
        public const string Version = "0.1.0";

        // 图集名称前缀。Atlas asset name prefix.
        public const string AtlasNamePrefix = "ATO_";

        // 图集最小边长（像素）。Minimum atlas side length in pixels.
        public const int MinAtlasSize = 64;

        // 桌面端图集最大边长；移动端最大边长。Desktop / mobile maximum atlas side length.
        public const int MaxAtlasSizeDesktop = 8192;
        public const int MaxAtlasSizeMobile = 4096;

        // 图集 padding 可选值（px）。Selectable atlas padding values in pixels.
        public static readonly int[] PaddingOptions = { 4, 8, 16, 32, 64 };
        public const int DefaultPaddingPx = 4;

        // 像素密度可选挡位（px/m）。Selectable pixel density tiers in px per meter.
        public static readonly int[] DensityOptions = { 512, 1024, 2048, 4096, 8192 };
        public const float DefaultMinDensityPxPerMeter = 2048f;
        public const float DefaultMaxDensityPxPerMeter = 4096f;

        // ---- 目标质量算法固定参数。Fixed parameters of the target quality algorithm. ----

        // MS-SSIM：包围盒短边 < 176px 的岛回退到单尺度 SSIM。
        // MS-SSIM: islands whose bounding box short side is < 176px fall back to single-scale SSIM.
        public const int MsSsimShortSideFallback = 176;

        // MS-SSIM：包围盒短边 < 11px 的岛直接忽略此参数（不透明贴图同理）。
        // MS-SSIM: islands whose bounding box short side is < 11px skip this metric entirely.
        public const int MsSsimShortSideIgnore = 11;

        // 纯色岛在目标质量 < 1 时直接缩到的最小尺寸：min(4, 原岛包围盒短边)。
        // Pure-color islands are shrunk to min(4, original short side) when target quality < 1.
        public const int PureColorMinSize = 4;

        // 岛光栅化位掩码粒度（px）。Island rasterization bitmask granularity in pixels.
        public const int RasterGranularityPx = 4;

        // 候选图集池 NPOT 边长步进（px）。NPOT candidate atlas pool side step in pixels.
        public const int NpotSideStep = 64;
    }
}
