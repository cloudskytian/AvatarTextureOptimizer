// Avatar Texture Optimizer / 头像贴图优化器
// Shared constants used by the whole pipeline.
// 全管线共享常量。

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Shared constants. / 共享常量。</summary>
    public static class ATOConsts
    {
        /// <summary>Package root path in the project. / 本包在项目内的根路径。</summary>
        public const string PackageRoot = "Packages/net.fosa.avatar-texture-optimizer";

        /// <summary>Built-in i18n directory relative to the project. / 内置 i18n 目录（相对项目）。</summary>
        public const string BuiltinI18nDir = PackageRoot + "/Editor/i18n";

        /// <summary>User-extensible i18n directory (Assets-relative). / 用户可扩展的 i18n 目录（Assets 内）。</summary>
        public const string UserI18nDir = "Assets/AvatarTextureOptimizer/I18n";

        /// <summary>Folder (inside Assets) that receives generated textures/atlases. / 生成贴图与图集的落地目录（Assets 内）。</summary>
        public const string GeneratedRoot = "Assets/AvatarTextureOptimizer-Generated";

        /// <summary>Atlas asset name prefix. / 图集资产名前缀。</summary>
        public const string AtlasPrefix = "ATO_";

        /// <summary>Scaled (non-atlas) texture name prefix. / 缩放（非图集）贴图名前缀。</summary>
        public const string ScaledPrefix = "ATO_Scaled_";

        /// <summary>Marker written to importer userData of generated assets (cache key storage). / 写入生成资产 importer.userData 的缓存标记。</summary>
        public const string CacheUserDataPrefix = "ATOv1|";

        /// <summary>Default max atlas edge on PC / PC 默认图集最大边长。</summary>
        public const int MaxAtlasPC = 8192;

        /// <summary>Default max atlas edge on mobile (Android/iOS) / 移动端（Android/iOS）默认图集最大边长。</summary>
        public const int MaxAtlasMobile = 4096;

        /// <summary>Minimum candidate atlas edge / 候选图集最小边长。</summary>
        public const int MinAtlasEdge = 64;

        /// <summary>Raster granularity for island packing (px per mask cell) / 装箱光栅粒度（每掩码格像素）。</summary>
        public const int RasterGranularity = 4;

        /// <summary>MS-SSIM is replaced by single-scale SSIM below this bbox short edge / 短边小于此值时 MS-SSIM 回退单尺度 SSIM。</summary>
        public const int MsSsimMinShortEdge = 176;

        /// <summary>Islands with bbox short edge below this skip SSIM entirely / 短边小于此值的岛完全跳过 SSIM 评估。</summary>
        public const int SsimIgnoreShortEdge = 11;

        /// <summary>Pure-color island max short edge when quality < 1 / 质量<1 时纯色岛缩到的最小短边（与原短边取小）。</summary>
        public const int PureColorMinSize = 4;

        /// <summary>Default build platform when none can be resolved / 无法解析时使用的默认平台。</summary>
        public const FOSA.AvatarTextureOptimizer.ATOPlatform DefaultPlatform = FOSA.AvatarTextureOptimizer.ATOPlatform.PC;
    }
}
