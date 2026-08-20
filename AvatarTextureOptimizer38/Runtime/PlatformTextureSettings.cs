using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Per-platform texture/atlas importer-related options. Folded until the platform override is enabled.
    /// 分平台贴图/图集导入相关选项。未勾选 platform override 时折叠。
    /// </summary>
    [Serializable]
    public struct PlatformTextureSettings
    {
        [Tooltip("Compression for opaque color atlases/textures. / 不透明主色压缩。")]
        public AtoCompressionFormat opaqueFormat;

        [Tooltip("Compression for atlases/textures that have alpha. / 带 alpha 的压缩。")]
        public AtoCompressionFormat transparentFormat;

        [Tooltip("Compression for normal maps. / 法线贴图压缩。")]
        public AtoCompressionFormat normalFormat;

        [Tooltip("Compression for grayscale (masks). / 灰度/蒙版压缩。")]
        public AtoCompressionFormat grayFormat;

        [Tooltip("Enable mipmaps + streaming (bound together for VRChat). / 同时开关 Mipmap 与 MipStreaming。")]
        public bool mipStreamingOpaque;

        public bool mipStreamingTransparent;
        public bool mipStreamingNormal;
        public bool mipStreamingGray;

        [Tooltip("Crunch compression when the selected format supports it. / 格式支持时使用 Crunch。")]
        public bool useCrunch;

        [Range(0, 100)]
        public int crunchQuality;

        [Range(0, 100)]
        public int compressorQuality;

        public static PlatformTextureSettings DefaultPc() => new PlatformTextureSettings
        {
            opaqueFormat = AtoCompressionFormat.BC7,
            transparentFormat = AtoCompressionFormat.BC7,
            normalFormat = AtoCompressionFormat.BC5,
            grayFormat = AtoCompressionFormat.BC4,
            mipStreamingOpaque = true,
            mipStreamingTransparent = true,
            mipStreamingNormal = true,
            mipStreamingGray = true,
            useCrunch = false,
            crunchQuality = 50,
            compressorQuality = 50
        };

        public static PlatformTextureSettings DefaultAndroid() => new PlatformTextureSettings
        {
            opaqueFormat = AtoCompressionFormat.ASTC_6x6,
            transparentFormat = AtoCompressionFormat.ASTC_6x6,
            normalFormat = AtoCompressionFormat.ASTC_4x4,
            grayFormat = AtoCompressionFormat.ASTC_6x6,
            mipStreamingOpaque = true,
            mipStreamingTransparent = true,
            mipStreamingNormal = true,
            mipStreamingGray = true,
            useCrunch = false,
            crunchQuality = 50,
            compressorQuality = 50
        };

        public static PlatformTextureSettings DefaultIos() => new PlatformTextureSettings
        {
            opaqueFormat = AtoCompressionFormat.ASTC_6x6,
            transparentFormat = AtoCompressionFormat.ASTC_6x6,
            normalFormat = AtoCompressionFormat.ASTC_4x4,
            grayFormat = AtoCompressionFormat.ASTC_6x6,
            mipStreamingOpaque = true,
            mipStreamingTransparent = true,
            mipStreamingNormal = true,
            mipStreamingGray = true,
            useCrunch = false,
            crunchQuality = 50,
            compressorQuality = 50
        };
    }
}
