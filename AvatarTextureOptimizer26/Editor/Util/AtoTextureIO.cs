using System;
using System.Reflection;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Safe decode / importer snapshot. Never guesses compressed GPU format as source of truth.
    /// 安全解码与导入设置快照。不以压缩 GPU 格式作为内容真值。
    /// </summary>
    public static class AtoTextureIO
    {
        public static string ImporterKey(Texture2D tex)
        {
            if (tex == null) return "null";
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path))
                return $"mem|{tex.width}x{tex.height}|{tex.format}|{tex.filterMode}|{tex.wrapMode}|srgb:{IsSrgb(tex)}";
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null)
                return $"asset|{path}|{tex.width}x{tex.height}|{tex.format}";
            return string.Join("|",
                path,
                imp.sRGBTexture,
                imp.filterMode,
                imp.wrapMode,
                imp.mipmapEnabled,
                imp.streamingMipmaps,
                imp.textureType,
                imp.textureCompression,
                imp.maxTextureSize,
                imp.npotScale,
                imp.alphaSource,
                imp.alphaIsTransparency,
                imp.crunchedCompression,
                imp.compressionQuality);
        }

        public static bool IsSrgb(Texture tex)
        {
            if (tex == null) return true;
            try
            {
                if (GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat)) return true;
            }
            catch { /* older */ }
            var path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path))
            {
                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp != null) return imp.sRGBTexture && imp.textureType != TextureImporterType.NormalMap;
            }
            return true;
        }

        public static bool IsNormalMap(Texture2D tex)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return false;
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            return imp != null && imp.textureType == TextureImporterType.NormalMap;
        }

        public static Texture2D EnsureReadable(AtoContext ctx, Texture2D src)
        {
            if (src == null) return null;
            if (src.isReadable)
            {
                try
                {
                    _ = src.GetPixels32();
                    return src;
                }
                catch { /* compressed-but-flagged-readable still needs blit */ }
            }

            var tmp = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32,
                IsSrgb(src) ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            Graphics.Blit(src, tmp);
            RenderTexture.active = tmp;
            var copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, !IsSrgb(src))
            {
                name = src.name + "_ATO_decode",
                filterMode = src.filterMode,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = src.anisoLevel
            };
            copy.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0, false);
            copy.Apply(false, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            ctx.RegisterTemp(copy);
            ObjectRegistry.RegisterReplacedObject(src, copy);
            return copy;
        }

        public static void ApplyImporterLike(Texture2D tex, AtoSafeFormat format, bool srgb, bool mipStreaming,
            AtoPlatform platform, bool isNormal, bool hasAlpha)
        {
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.wrapModeU = TextureWrapMode.Clamp;
            tex.wrapModeV = TextureWrapMode.Clamp;
            // Read/Write off for atlases: Apply(updateMipmaps, makeNoLongerReadable=true) later.
            // 图集关闭 Read/Write：稍后 Apply(true, true)。

            var unityFormat = ResolveFormat(format, platform, isNormal, hasAlpha, srgb);
            // Actual GPU compress happens at Unity import/build. Runtime Texture2D.Compress is editor-only fallback.
            // 真正的 GPU 压缩发生在 Unity 导入/构建。这里用编辑器 Compress 作为回退。
            try
            {
                if (unityFormat == TextureFormat.DXT1 || unityFormat == TextureFormat.DXT5 ||
                    unityFormat == TextureFormat.BC4 || unityFormat == TextureFormat.BC5 ||
                    unityFormat == TextureFormat.BC7)
                {
                    EditorUtility.CompressTexture(tex, unityFormat, TextureCompressionQuality.Normal);
                }
            }
            catch (Exception e)
            {
                AtoLog.Warn($"Compress fallback for {tex.name}: {e.Message}");
            }

            if (mipStreaming)
            {
                tex.Apply(true, false);
                TrySetStreamingMipmaps(tex, true);
                tex.Apply(true, true);
            }
            else
            {
                tex.Apply(false, true);
            }
        }

        public static TextureFormat ResolveFormat(AtoSafeFormat want, AtoPlatform platform, bool isNormal,
            bool hasAlpha, bool srgb)
        {
            if (want == AtoSafeFormat.Auto)
            {
                if (isNormal)
                    return platform == AtoPlatform.PC ? TextureFormat.BC5 : TextureFormat.ASTC_6x6;
                if (hasAlpha)
                    return platform == AtoPlatform.PC ? TextureFormat.DXT5 : TextureFormat.ASTC_6x6;
                return platform == AtoPlatform.PC ? TextureFormat.DXT1 : TextureFormat.ASTC_6x6;
            }

            // Safety: never drop alpha. / 安全：绝不丢掉 alpha。
            if (hasAlpha && (want == AtoSafeFormat.DXT1 || want == AtoSafeFormat.RGB24 ||
                             want == AtoSafeFormat.ETC2_RGB || want == AtoSafeFormat.PVRTC_RGB4 ||
                             want == AtoSafeFormat.BC4))
            {
                AtoLog.Warn($"Rejected format {want} for a texture with alpha; falling back.");
                want = platform == AtoPlatform.PC ? AtoSafeFormat.DXT5 : AtoSafeFormat.ASTC_6x6;
            }

            // NPOT + PVRTC is invalid. / NPOT 与 PVRTC 不兼容。
            if (want == AtoSafeFormat.PVRTC_RGB4 || want == AtoSafeFormat.PVRTC_RGBA4)
            {
                if (platform == AtoPlatform.iOS)
                    want = hasAlpha ? AtoSafeFormat.ASTC_6x6 : AtoSafeFormat.ASTC_6x6;
            }

            return want switch
            {
                AtoSafeFormat.RGBA32 => TextureFormat.RGBA32,
                AtoSafeFormat.RGB24 => TextureFormat.RGB24,
                AtoSafeFormat.RGBAHalf => TextureFormat.RGBAHalf,
                AtoSafeFormat.DXT1 => TextureFormat.DXT1,
                AtoSafeFormat.DXT5 => TextureFormat.DXT5,
                AtoSafeFormat.BC4 => TextureFormat.BC4,
                AtoSafeFormat.BC5 => TextureFormat.BC5,
                AtoSafeFormat.BC7 => TextureFormat.BC7,
                AtoSafeFormat.ETC2_RGB => TextureFormat.ETC2_RGB,
                AtoSafeFormat.ETC2_RGBA8 => TextureFormat.ETC2_RGBA8,
                AtoSafeFormat.ASTC_4x4 => TextureFormat.ASTC_4x4,
                AtoSafeFormat.ASTC_5x5 => TextureFormat.ASTC_5x5,
                AtoSafeFormat.ASTC_6x6 => TextureFormat.ASTC_6x6,
                AtoSafeFormat.ASTC_8x8 => TextureFormat.ASTC_8x8,
                AtoSafeFormat.PVRTC_RGB4 => TextureFormat.PVRTC_RGB4,
                AtoSafeFormat.PVRTC_RGBA4 => TextureFormat.PVRTC_RGBA4,
                _ => TextureFormat.RGBA32
            };
        }

        public static bool FormatAllowedForNpot(AtoSafeFormat f)
        {
            return f != AtoSafeFormat.PVRTC_RGB4 && f != AtoSafeFormat.PVRTC_RGBA4;
        }

        private static void TrySetStreamingMipmaps(Texture2D tex, bool on)
        {
            try
            {
                var p = typeof(Texture).GetProperty("streamingMipmaps",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                p?.SetValue(tex, on);
            }
            catch { /* not available on all versions */ }
        }
    }
}
