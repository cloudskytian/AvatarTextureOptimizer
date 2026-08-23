namespace Fosa.AvatarTextureOptimizer
{
    public enum ATOQualityPreset { Performance, Balanced, High, Ultra, NearLossless, Custom }
    public enum ATOPlatform { PC, Android, IOS }
    public enum ATOMinimumPadding { Pixels4 = 4, Pixels8 = 8, Pixels16 = 16, Pixels32 = 32, Pixels64 = 64 }
    public enum ATOPixelDensity { Density512 = 512, Density1024 = 1024, Density2048 = 2048, Density4096 = 4096, Density8192 = 8192 }
    public enum ATOTextureKind
    {
        ColorOpaque = 0,
        ColorAlpha = 1,
        Normal = 2,
        Grayscale = 3,
        // Straight (non-premultiplied) RGBA data such as packed masks or non-surface alpha.
        ColorRgbaData = 4
    }

    public enum ATOSurfaceAlphaUsage
    {
        None = 0,
        TextureAlpha = 1,
        UnsupportedComposite = 2
    }

    // Only formats with a verified Unity TextureFormat mapping are exposed. / 只公开已验证可映射到 Unity TextureFormat 的格式。
    public enum ATOCompression
    {
        Auto,
        UncompressedRGBA32,
        UncompressedRGB24,
        BC7,
        BC5,
        DXT1,
        DXT5,
        ETC2RGB,
        ETC2RGBA8,
        ASTC4x4,
        ASTC6x6
    }

    public enum ATOLanguage { Auto, English, SimplifiedChinese }
    public enum ATOAlphaMode { Opaque, Cutout, Blend }
}
