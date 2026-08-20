using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// EN: Global compile-time constants shared between runtime and editor assemblies.
    /// ZH: 运行时与编辑器程序集共享的全局常量。
    /// </summary>
    public static class ATOConstants
    {
        /// <summary>EN: Package identifier. ZH: 包名。</summary>
        public const string PackageName = "net.fosa.avatar-texture-optimizer";

        /// <summary>EN: Display name. ZH: 显示名。</summary>
        public const string DisplayName = "Avatar Texture Optimizer";

        /// <summary>EN: NDMF plugin qualified name. ZH: NDMF 插件限定名。</summary>
        public const string PluginQualifiedName = "net.fosa.avatar-texture-optimizer";

        /// <summary>EN: Prefix for every log line. ZH: 所有日志行的前缀。</summary>
        public const string LogPrefix = "[ATO]";

        /// <summary>EN: Prefix for every generated atlas asset name. ZH: 所有生成图集资产名的前缀。</summary>
        public const string AtlasNamePrefix = "ATO_";

        /// <summary>EN: Qualified name of Avatar Optimizer's NDMF plugin (we must run before it).
        /// ZH: AAO 的 NDMF 插件限定名（我们必须在它之前运行）。</summary>
        public const string AAOPluginQualifiedName = "com.anatawa12.avatar-optimizer";

        /// <summary>EN: Qualified name of Modular Avatar's NDMF plugin (we must run after it).
        /// ZH: MA 的 NDMF 插件限定名（我们必须在它之后运行）。</summary>
        public const string MAPluginQualifiedName = "nadena.dev.modular-avatar";

        /// <summary>EN: Rasterization granularity for island packing, in pixels. ZH: 装箱光栅化粒度（像素）。</summary>
        public const int RasterGranularity = 4;

        /// <summary>EN: Minimum atlas side length. ZH: 候选图集最小边长。</summary>
        public const int MinAtlasSide = 64;

        /// <summary>EN: Maximum atlas side length on PC. ZH: PC 端候选图集最大边长。</summary>
        public const int MaxAtlasSidePC = 8192;

        /// <summary>EN: Maximum atlas side length on mobile (Android / iOS). ZH: 移动端候选图集最大边长。</summary>
        public const int MaxAtlasSideMobile = 4096;

        /// <summary>EN: Islands whose original bounding box short side is under this are ignored by SSIM.
        /// ZH: 原尺寸包围盒短边小于该值的岛直接忽略 SSIM 参数。</summary>
        public const int SsimIgnoreShortSide = 11;

        /// <summary>EN: Islands whose short side is under this fall back from MS-SSIM to single scale SSIM.
        /// ZH: 短边小于该值的岛从 MS-SSIM 回退到单尺度 SSIM。</summary>
        public const int MsSsimMinShortSide = 176;

        /// <summary>EN: Absolute floor for a solid-color island short side. ZH: 纯色岛短边的绝对下限。</summary>
        public const int SolidIslandMinSide = 4;

        /// <summary>EN: Maximum binary search iterations per axis. ZH: 每个轴的二分搜索最大迭代次数。</summary>
        public const int MaxBinarySearchIterations = 8;
    }
}
