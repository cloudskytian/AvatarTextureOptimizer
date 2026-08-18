// Avatar Texture Optimizer (ATO)
// Shared constants. / 共享常量。

namespace NetFosa.ATO
{
    /// <summary>
    /// Shared constants used across runtime and editor code.
    /// 运行时与编辑器代码共享的常量。
    /// </summary>
    public static class ATOConstants
    {
        /// <summary>Log prefix. / 日志前缀。</summary>
        public const string LogPrefix = "[ATO]";

        /// <summary>Prefix used for generated atlas asset names. / 生成的图集资产名前缀。</summary>
        public const string AtlasNamePrefix = "ATO_";

        /// <summary>Minimum atlas side length (pixels). / 图集最小边长（像素）。</summary>
        public const int MinAtlasSize = 64;

        /// <summary>Maximum atlas side length on desktop (pixels). / 桌面端图集最大边长（像素）。</summary>
        public const int MaxAtlasSizeDesktop = 8192;

        /// <summary>Maximum atlas side length on mobile (pixels). / 移动端图集最大边长（像素）。</summary>
        public const int MaxAtlasSizeMobile = 4096;

        /// <summary>Rasterization granularity used by the atlas packer (pixels per cell). / 装箱光栅化粒度（每格像素）。</summary>
        public const int RasterCellSize = 4;

        /// <summary>Maximum supported UV channels. / 支持的 UV 通道数。</summary>
        public const int MaxUvChannels = 8;

        /// <summary>Default pixel density range (px per meter). / 默认像素密度范围（每米像素）。</summary>
        public const int DefaultPixelDensityMin = 2048;
        public const int DefaultPixelDensityMax = 4096;

        /// <summary>Available pixel-density presets. / 可选的像素密度挡位。</summary>
        public static readonly int[] PixelDensityOptions = { 512, 1024, 2048, 4096, 8192 };

        /// <summary>Available padding (px) options between packed islands. / 可选的岛间距挡位。</summary>
        public static readonly int[] PaddingOptions = { 4, 8, 16, 32, 64 };

        /// <summary>Islands whose bounding-box short side is below this are ignored by MS-SSIM (px). / 包围盒短边小于该值的岛忽略 MS-SSIM 指标（像素）。</summary>
        public const int MsSsimIgnoreShortSide = 11;

        /// <summary>Islands whose bounding-box short side is below this fall back to single-scale SSIM (px). / 包围盒短边小于该值的岛回退到单尺度 SSIM（像素）。</summary>
        public const int SsimFallbackShortSide = 176;

        /// <summary>Packing rotation steps in degrees (normal maps excluded). / 装箱旋转步进角度（法线贴图除外）。</summary>
        public const int RotationStepDegrees = 90;
    }
}
