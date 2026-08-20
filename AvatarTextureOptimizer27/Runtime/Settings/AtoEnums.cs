namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>Build platform override. / 构建平台覆盖。</summary>
    public enum AtoPlatform
    {
        Auto = 0,
        PC = 1,
        Android = 2,
        iOS = 3
    }

    /// <summary>
    /// Quality preset. Values derived from perceptual literature (MS-SSIM ~0.98 near-lossless, CIEDE2000 ~1 JND).
    /// 质量挡位：参考 MS-SSIM 近无损约 0.98、CIEDE2000 约 1 JND。
    /// </summary>
    public enum AtoQualityPreset
    {
        Ultra = 0,
        High = 1,
        Medium = 2,
        Low = 3,
        Custom = 4
    }

    public enum AtoAlphaMode
    {
        Opaque = 0,
        Cutout = 1,
        Blend = 2
    }

    public enum AtoTextureSemantic
    {
        Albedo = 0,
        Normal = 1,
        Mask = 2,
        MetallicGloss = 3,
        Emission = 4,
        Gray = 5,
        Unknown = 6
    }

    public enum AtoMinPadding
    {
        Px4 = 4,
        Px8 = 8,
        Px16 = 16,
        Px32 = 32,
        Px64 = 64
    }

    public enum AtoPixelDensityPreset
    {
        Px512 = 512,
        Px1024 = 1024,
        Px2048 = 2048,
        Px4096 = 4096,
        Px8192 = 8192
    }

    public enum AtoSafeOpaqueFormat
    {
        Auto = 0,
        DXT1 = 1,
        BC7 = 2,
        ASTC_6x6 = 3,
        ETC2_RGB = 4,
        RGBA32 = 5
    }

    public enum AtoSafeAlphaFormat
    {
        Auto = 0,
        DXT5 = 1,
        BC7 = 2,
        ASTC_6x6 = 3,
        ETC2_RGBA8 = 4,
        RGBA32 = 5
    }

    public enum AtoSafeNormalFormat
    {
        Auto = 0,
        BC5 = 1,
        DXT5 = 2,
        ASTC_6x6 = 3,
        RGBA32 = 4
    }

    public enum AtoSafeGrayFormat
    {
        Auto = 0,
        BC4 = 1,
        DXT1 = 2,
        ASTC_6x6 = 3,
        R8 = 4,
        RGBA32 = 5
    }
}
