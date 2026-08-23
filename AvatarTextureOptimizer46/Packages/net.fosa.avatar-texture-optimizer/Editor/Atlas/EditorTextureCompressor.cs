// SPDX-License-Identifier: MIT
// EN: Thin wrapper over EditorUtility.CompressTexture with mip streaming handling.
// ZH: 对 EditorUtility.CompressTexture 的轻量封装，并处理 mip streaming。

using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>
    /// EN: Compresses generated textures in memory. The generated textures are never assets on disk with
    ///     an importer, so the platform format has to be applied directly.
    /// ZH: 在内存中压缩生成的贴图。生成的贴图并不是硬盘上带导入器的资产，
    ///     因此必须直接应用平台格式。
    /// </summary>
    public static class EditorTextureCompressor
    {
        private const string Stage = "Compress";

        /// <summary>
        /// EN: Compresses in place. Sizes that a block format cannot represent fall back to RGBA32 rather
        ///     than producing a corrupted texture.
        /// ZH: 就地压缩。块压缩格式无法表示的尺寸会回退到 RGBA32，而不是产生损坏的贴图。
        /// </summary>
        public static void Compress(Texture2D texture, TextureFormat format)
        {
            if (format == TextureFormat.RGBA32) return;

            if (RequiresBlockAlignment(format) && (texture.width % 4 != 0 || texture.height % 4 != 0))
            {
                AtoLog.Warning(Stage,
                    $"'{texture.name}' is {texture.width}x{texture.height}, which {format} cannot encode; keeping RGBA32.");
                return;
            }

            EditorUtility.CompressTexture(texture, format, TextureCompressionQuality.Normal);
            AtoLog.Debug_(Stage, $"'{texture.name}' compressed to {format}");
        }

        private static bool RequiresBlockAlignment(TextureFormat format)
        {
            var n = format.ToString();
            return n.StartsWith("DXT") || n.StartsWith("BC") || n.StartsWith("ETC") || n.StartsWith("ASTC");
        }
    }
}
