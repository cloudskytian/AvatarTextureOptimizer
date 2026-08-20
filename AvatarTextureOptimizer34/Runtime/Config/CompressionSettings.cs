// AvatarTextureOptimizer - CompressionSettings
// EN: Safe compression format choices per texture category. Platform validation happens at build time.
// CN: 按贴图分类的安全压缩格式选项。平台合法性在构建时校验。
using System;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// EN: Texture categories used for compression/format decisions.
    /// CN: 用于压缩格式决策的贴图分类。
    /// </summary>
    public enum TextureCategory
    {
        Opaque = 0,        // 不透明主色（图集无 alpha 通道）
        OpaqueAlpha = 1,   // 透明主色（图集有 alpha 通道）
        Normal = 2,        // 法线贴图
        Gray = 3           // 灰度/蒙版贴图
    }

    /// <summary>
    /// EN: Platform-agnostic format enum. Invalid combos are rejected with safe fallback + warning.
    /// CN: 平台无关格式枚举。非法组合在构建时安全回退并告警。
    /// </summary>
    public enum AtoCompressionFormat
    {
        Auto = 0,       // 平台默认最优
        RGBA32 = 1,     // 无压缩
        RGB24 = 2,
        BC1 = 10,       // PC
        BC3 = 11,
        BC4 = 12,
        BC5 = 13,
        BC7 = 14,
        ETC2_RGB = 20,  // Android
        ETC2_RGBA = 21,
        ETC1 = 22,
        ASTC_4x4 = 30,  // Android / iOS(14+)
        ASTC_6x6 = 31,
        ASTC_8x8 = 32,
        ASTC_10x10 = 33,
        ASTC_12x12 = 34,
        PVRTC_RGB4 = 40, // iOS
        PVRTC_RGBA4 = 41,
        PVRTC_RGB2 = 42,
        PVRTC_RGBA2 = 43
    }

    /// <summary>
    /// EN: Per-category compression choices. Normal/Gray are forced to formats with alpha when needed at build time.
    /// CN: 分类压缩选择。法线/灰度在需要时于构建期强制使用带 alpha 的格式。
    /// </summary>
    [Serializable]
    public class CompressionSettings
    {
        public AtoCompressionFormat opaque = AtoCompressionFormat.Auto;       // 无 alpha
        public AtoCompressionFormat opaqueAlpha = AtoCompressionFormat.Auto;  // 有 alpha
        public AtoCompressionFormat normal = AtoCompressionFormat.Auto;
        public AtoCompressionFormat gray = AtoCompressionFormat.Auto;

        public AtoCompressionFormat For(TextureCategory cat) => cat switch
        {
            TextureCategory.Opaque => opaque,
            TextureCategory.OpaqueAlpha => opaqueAlpha,
            TextureCategory.Normal => normal,
            _ => gray
        };
    }
}
