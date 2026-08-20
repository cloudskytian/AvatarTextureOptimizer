// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System;

namespace AvatarTextureOptimizer
{
    /// <summary>
    /// Per-texture-category compression + mipmap settings.
    /// 按贴图类别的压缩与 mipmap 设置。
    /// </summary>
    [Serializable]
    public class ATOCategorySettings
    {
        /// <summary>The texture category this applies to. 该设置作用的贴图类别。</summary>
        public ATOTextureCategory category;

        /// <summary>
        /// Compression format. Only "safe" values for this category are selectable in UI
        /// and validated again at build time. 压缩格式。UI 中仅提供该类别安全的枚举项，
        /// 构建时再次校验。
        /// </summary>
        public ATOCompressionFormat format = ATOCompressionFormat.Auto;

        /// <summary>
        /// Master toggle that binds Mipmap and MipStreaming together (VRChat requires
        /// MipStreaming whenever Mipmaps are enabled). 同时控制 Mipmap 与 MipStreaming 的总开关
        /// （VRChat 要求开启 Mipmap 时必须开启 MipStreaming，二者绑定）。
        /// </summary>
        public bool mipmapsAndStreaming = true;
    }

    /// <summary>
    /// Global compression settings: one entry per texture category.
    /// 全局压缩设置：每个贴图类别一项。
    /// </summary>
    [Serializable]
    public class ATOCompressionSettings
    {
        public ATOCategorySettings[] categories =
        {
            new ATOCategorySettings { category = ATOTextureCategory.Albedo, format = ATOCompressionFormat.BC7 },
            new ATOCategorySettings { category = ATOTextureCategory.Normal, format = ATOCompressionFormat.BC5 },
            new ATOCategorySettings { category = ATOTextureCategory.Mask, format = ATOCompressionFormat.BC4 },
            new ATOCategorySettings { category = ATOTextureCategory.Emission, format = ATOCompressionFormat.BC7 },
            new ATOCategorySettings { category = ATOTextureCategory.Other, format = ATOCompressionFormat.BC7 },
        };

        public ATOCategorySettings Get(ATOTextureCategory c)
        {
            foreach (var s in categories)
                if (s.category == c) return s;
            return null;
        }
    }

    /// <summary>
    /// Per-platform overridable settings. Mirrors Unity's platform override concept:
    /// parameters that are platform-limited (e.g. atlas compression format) can be
    /// overridden per platform.
    /// 各平台可覆盖设置。对应 Unity 的 platform override：受平台限制的参数
    /// （如图集压缩格式）可逐平台覆盖。
    /// </summary>
    [Serializable]
    public class ATOPlatformSettings
    {
        /// <summary>Whether this platform overrides the global settings. 是否覆盖全局设置。</summary>
        public bool overrideEnabled = false;

        public ATOCompressionSettings compression = new ATOCompressionSettings();

        /// <summary>Max atlas edge for this platform (PC 8192, mobile 4096). 该平台图集最大边长。</summary>
        public int maxAtlasEdge = 4096;

        /// <summary>Whether NPOT atlas sizes are allowed on this platform. 该平台是否允许 NPOT。</summary>
        public bool allowNPOT = false;
    }

    /// <summary>
    /// Platform override container (PC / Android / iOS).
    /// 平台覆盖容器（PC / Android / iOS）。
    /// </summary>
    [Serializable]
    public class ATOPlatformOverride
    {
        public ATOPlatformSettings pc = new ATOPlatformSettings { maxAtlasEdge = 8192 };
        public ATOPlatformSettings android = new ATOPlatformSettings { maxAtlasEdge = 4096 };
        public ATOPlatformSettings ios = new ATOPlatformSettings { maxAtlasEdge = 4096 };

        public ATOPlatformSettings Get(ATOPlatform p)
        {
            switch (p)
            {
                case ATOPlatform.Android: return android;
                case ATOPlatform.iOS: return ios;
                default: return pc;
            }
        }
    }
}
