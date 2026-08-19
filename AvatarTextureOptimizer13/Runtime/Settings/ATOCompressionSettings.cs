// ATO — Avatar Texture Optimizer
// Safe compression format choices, separated by texture kind.
// 按贴图类型区分的"安全"压缩格式选择。
//
// The enum below intentionally does NOT expose raw TextureImporterFormat values:
// a safe enumeration keeps the user from selecting formats that are invalid for the
// current platform or for the texture content (e.g. an alpha-less format for a
// transparent texture). The concrete TextureImporterFormat is resolved at build time
// in the editor layer, with safety fallbacks.
// 该枚举刻意不直接暴露 TextureImporterFormat 原始值：
// "安全枚举"避免用户选中对当前平台或贴图内容无效的格式（例如给透明贴图选无 alpha 的格式）。
// 具体的 TextureImporterFormat 在编辑器层构建时解析，并带安全回退。

using System;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// Safe compression quality tiers. 安全压缩质量挡位。
    /// </summary>
    public enum ATOSafeCompression
    {
        /// <summary>Auto — pick the platform- and content-appropriate default. 自动——按平台与内容选择合适默认。</summary>
        Auto = 0,
        /// <summary>Uncompressed RGBA/RGB (largest, lossless). 不压缩 RGBA/RGB（体积最大，无损）。</summary>
        NoCompression = 1,
        /// <summary>Low quality compression (smallest). 低质量压缩（体积最小）。</summary>
        LowCompression = 2,
        /// <summary>Normal quality compression. 普通质量压缩。</summary>
        NormalCompression = 3,
        /// <summary>High quality compression (larger, cleaner). 高质量压缩（体积较大，更干净）。</summary>
        HighCompression = 4,
        /// <summary>Normal-map compression (BC5/DXT5nm on PC, ASTC/EAC on mobile). 法线贴图压缩（PC 为 BC5/DXT5nm，移动端为 ASTC/EAC）。</summary>
        NormalMapCompression = 5,
    }

    /// <summary>
    /// Per-texture-kind compression choices. 按贴图类型区分的压缩选择。
    /// </summary>
    [Serializable]
    public class ATOCompressionSettings
    {
        /// <summary>Opaque color textures. 不透明主色贴图。</summary>
        [Tooltip("Compression for opaque color textures. 不透明主色贴图的压缩。")]
        public ATOSafeCompression color = ATOSafeCompression.Auto;

        /// <summary>Transparent color textures. 透明主色贴图。</summary>
        [Tooltip("Compression for transparent color textures. 透明主色贴图的压缩。")]
        public ATOSafeCompression colorTransparent = ATOSafeCompression.Auto;

        /// <summary>Normal maps. 法线贴图。</summary>
        [Tooltip("Compression for normal maps. 法线贴图的压缩。")]
        public ATOSafeCompression normal = ATOSafeCompression.Auto;

        /// <summary>Grayscale / mask textures. 灰度 / 蒙版贴图。</summary>
        [Tooltip("Compression for grayscale / mask textures. 灰度 / 蒙版贴图的压缩。")]
        public ATOSafeCompression grayscale = ATOSafeCompression.Auto;

        /// <summary>
        /// Force single-channel storage for grayscale textures.
        /// Even when enabled, multi-channel content is saved multi-channel with a warning.
        /// 强制灰度贴图以单通道存储。即便开启，多通道内容仍会以多通道保存并警告。
        /// </summary>
        [Tooltip("Force single-channel storage for grayscale textures. 强制灰度贴图以单通道存储。")]
        public bool grayscaleForceSingleChannel = false;

        public ATOCompressionSettings Clone()
        {
            return (ATOCompressionSettings)MemberwiseClone();
        }
    }
}
