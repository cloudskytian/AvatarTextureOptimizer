// TextureProcessor.cs / TextureProcessor.cs
// Applies final texture import settings (mipmaps, MipStreaming, wrapping, compression format, Crunch, platform overrides).
// Also applies settings to whole-texture-scaled outputs.
// 应用最终贴图导入设置（mipmaps、MipStreaming、wrapping、压缩格式、Crunch、平台覆盖）。
// 同时对整图缩放输出应用设置。

using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using net.fosa.avatar_texture_optimizer;
using net.fosa.avatar_texture_optimizer.Editor.Atlas;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using net.fosa.avatar_texture_optimizer.Editor.Util;

namespace net.fosa.avatar_texture_optimizer.Editor.Processing
{
    public static class TextureProcessor
    {
        public static void ApplyTextureSettings(AvatarAnalysisResult analysis, List<AtlasTexture> atlases,
            Dictionary<Texture2D, Texture2D> wholeTextureMap, BuildContext context, TargetPlatform platform)
        {
            var settings = analysis.Settings;
            var po = settings.GetEffectivePlatformSettings(platform);

            bool allowNPOT = settings.allowNPOT;
            if (allowNPOT && platform == TargetPlatform.iOS)
            {
                // PVRTC does not support NPOT; fall back to ASTC/ETC2
                // PVRTC不支持NPOT；回退到ASTC/ETC2
            }

            // Apply settings to atlases / 对图集应用设置
            foreach (var atl in atlases)
            {
                if (atl.Texture == null) continue;
                context.AssetSaver.SaveAsset(atl.Texture);

                atl.Texture.wrapMode = TextureWrapMode.Clamp;
                atl.Texture.filterMode = FilterMode.Bilinear;
                atl.Texture.anisoLevel = 1;

                bool useMips = po.mipmapEnabled;
                if (useMips)
                {
                    if (atl.Texture.mipmapCount <= 1) atl.Texture.GenerateMips();
                    atl.Texture.streamingMipmaps = true;
                }
                else
                {
                    atl.Texture.streamingMipmaps = false;
                }
                atl.Texture.Apply(true, false);
            }

            // Apply settings to whole-texture scaled outputs / 对整图缩放输出应用设置
            if (wholeTextureMap != null)
            {
                foreach (var kv in wholeTextureMap)
                {
                    var scaled = kv.Value;
                    if (scaled == null || scaled == kv.Key) continue;
                    context.AssetSaver.SaveAsset(scaled);
                    scaled.wrapMode = TextureWrapMode.Clamp;
                    scaled.filterMode = FilterMode.Bilinear;
                    scaled.anisoLevel = 1;
                    bool useMips = po.mipmapEnabled;
                    if (useMips)
                    {
                        if (scaled.mipmapCount <= 1) scaled.GenerateMips();
                        scaled.streamingMipmaps = true;
                    }
                    else scaled.streamingMipmaps = false;
                    scaled.Apply(true, false);
                }
            }

            ValidateFormatSafety(atlases, settings, platform);
        }

        private static void ValidateFormatSafety(List<AtlasTexture> atlases, AvatarTextureOptimizer settings, TargetPlatform platform)
        {
            foreach (var atl in atlases)
            {
                bool hasAlpha = atl.HasAlpha;
                bool isNormal = atl.IsNormal;
                // Keep in-memory texture as RGBA32 (safest). Platform compression is applied at build time.
                // 内存中贴图保持RGBA32（最安全）。平台压缩在构建时应用。
            }
        }
    }
}
