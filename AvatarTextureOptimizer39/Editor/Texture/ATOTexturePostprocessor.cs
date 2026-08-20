// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using UnityEditor;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Texture
{
    /// <summary>
    /// Post-processor for ATO-generated textures ("ATO_" prefix). Enforces the
    /// Mipmap ↔ MipStreaming binding (VRChat requires MipStreaming whenever Mipmaps are
    /// enabled), turns Read/Write off, and forces Clamp — settings that are TextureImporter
    /// concerns and can only be applied once the texture is saved as an asset.
    ///
    /// ATO 生成贴图（"ATO_" 前缀）的后处理器。强制执行 Mipmap ↔ MipStreaming 绑定
    /// （VRChat 要求开启 Mipmap 时必须开启 MipStreaming）、关闭 Read/Write、强制 Clamp
    /// —— 这些是 TextureImporter 设置，只能在贴图保存为资产后应用。
    /// </summary>
    public sealed class ATOTexturePostprocessor : AssetPostprocessor
    {
        private void OnPostprocessTexture(Texture2D texture)
        {
            if (texture == null || !texture.name.StartsWith("ATO_")) return;

            var importer = assetImporter as TextureImporter;
            if (importer == null) return;

            bool changed = false;

            // Mipmap ↔ MipStreaming binding. Mipmap 与 MipStreaming 绑定。
            if (importer.mipmapEnabled && !importer.streamingMipmaps)
            {
                importer.streamingMipmaps = true;
                changed = true;
            }
            else if (!importer.mipmapEnabled && importer.streamingMipmaps)
            {
                importer.streamingMipmaps = false;
                changed = true;
            }

            // Read/Write off. 关闭 Read/Write。
            if (importer.isReadable)
            {
                importer.isReadable = false;
                changed = true;
            }

            // Forced Clamp. 强制 Clamp。
            if (importer.wrapMode != TextureWrapMode.Clamp)
            {
                importer.wrapMode = TextureWrapMode.Clamp;
                changed = true;
            }

            if (changed)
            {
                importer.SaveAndReimport();
            }
        }
    }
}
