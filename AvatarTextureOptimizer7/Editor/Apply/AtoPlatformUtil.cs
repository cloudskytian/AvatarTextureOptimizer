using Fosa.AvatarTextureOptimizer;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    public static class AtoPlatformUtil
    {
        public static AtoPlatform Resolve(AtoPlatform requested)
        {
            if (requested != AtoPlatform.Auto) return requested;
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return AtoPlatform.Android;
                case BuildTarget.iOS: return AtoPlatform.iOS;
                default: return AtoPlatform.PC;
            }
        }

        public static int MaxAtlasEdge(AtoPlatform platform)
        {
            // Mobile VRChat hard-caps at 4096. / 移动端 VRChat 硬上限 4096。
            return platform == AtoPlatform.PC ? 8192 : 4096;
        }

        public static bool FormatAllowed(AtoSafeFormat format, AtoPlatform platform, bool npot, bool needsAlpha)
        {
            if (format == AtoSafeFormat.Auto) return true;
            if (needsAlpha && IsOpaqueOnly(format)) return false;
            if (npot && (format == AtoSafeFormat.PVRTC_RGB4 || format == AtoSafeFormat.PVRTC_RGBA4))
                return false;

            switch (platform)
            {
                case AtoPlatform.PC:
                    return format == AtoSafeFormat.RGBA32 || format == AtoSafeFormat.RGB24 ||
                           format == AtoSafeFormat.RGBAHalf ||
                           format == AtoSafeFormat.DXT1 || format == AtoSafeFormat.DXT5 ||
                           format == AtoSafeFormat.BC4 || format == AtoSafeFormat.BC5 ||
                           format == AtoSafeFormat.BC7 ||
                           format == AtoSafeFormat.DXT1Crunched || format == AtoSafeFormat.DXT5Crunched;
                case AtoPlatform.Android:
                    return format == AtoSafeFormat.RGBA32 || format == AtoSafeFormat.RGB24 ||
                           format == AtoSafeFormat.ETC2_RGB || format == AtoSafeFormat.ETC2_RGBA8 ||
                           format == AtoSafeFormat.ASTC_4x4 || format == AtoSafeFormat.ASTC_5x5 ||
                           format == AtoSafeFormat.ASTC_6x6 || format == AtoSafeFormat.ASTC_8x8;
                case AtoPlatform.iOS:
                    if (format == AtoSafeFormat.PVRTC_RGB4 || format == AtoSafeFormat.PVRTC_RGBA4)
                        return !npot;
                    return format == AtoSafeFormat.RGBA32 || format == AtoSafeFormat.RGB24 ||
                           format == AtoSafeFormat.ASTC_4x4 || format == AtoSafeFormat.ASTC_5x5 ||
                           format == AtoSafeFormat.ASTC_6x6 || format == AtoSafeFormat.ASTC_8x8;
                default:
                    return true;
            }
        }

        public static bool IsOpaqueOnly(AtoSafeFormat format)
        {
            switch (format)
            {
                case AtoSafeFormat.RGB24:
                case AtoSafeFormat.DXT1:
                case AtoSafeFormat.DXT1Crunched:
                case AtoSafeFormat.ETC2_RGB:
                case AtoSafeFormat.BC4:
                case AtoSafeFormat.PVRTC_RGB4:
                    return true;
                default:
                    return false;
            }
        }

        public static TextureFormat ToUnity(AtoSafeFormat format, bool needsAlpha, AtoTextureKind kind, AtoPlatform platform, bool npot)
        {
            if (format != AtoSafeFormat.Auto && FormatAllowed(format, platform, npot, needsAlpha))
            {
                return ToUnityRaw(format);
            }

            // Safe defaults per platform / kind. / 按平台与类型的安全默认。
            if (kind == AtoTextureKind.Normal)
            {
                if (platform == AtoPlatform.PC) return TextureFormat.BC5;
                return TextureFormat.ASTC_4x4;
            }

            if (kind == AtoTextureKind.Gray && !needsAlpha)
            {
                if (platform == AtoPlatform.PC) return TextureFormat.BC4;
                return needsAlpha ? TextureFormat.ASTC_4x4 : TextureFormat.ASTC_6x6;
            }

            if (platform == AtoPlatform.PC)
                return needsAlpha ? TextureFormat.DXT5 : TextureFormat.DXT1;
            return needsAlpha ? TextureFormat.ASTC_4x4 : TextureFormat.ASTC_6x6;
        }

        public static TextureFormat ToUnityRaw(AtoSafeFormat format)
        {
            switch (format)
            {
                case AtoSafeFormat.RGBA32: return TextureFormat.RGBA32;
                case AtoSafeFormat.RGB24: return TextureFormat.RGB24;
                case AtoSafeFormat.RGBAHalf: return TextureFormat.RGBAHalf;
                case AtoSafeFormat.DXT1: return TextureFormat.DXT1;
                case AtoSafeFormat.DXT5: return TextureFormat.DXT5;
                case AtoSafeFormat.BC4: return TextureFormat.BC4;
                case AtoSafeFormat.BC5: return TextureFormat.BC5;
                case AtoSafeFormat.BC7: return TextureFormat.BC7;
                case AtoSafeFormat.DXT1Crunched: return TextureFormat.DXT1Crunched;
                case AtoSafeFormat.DXT5Crunched: return TextureFormat.DXT5Crunched;
                case AtoSafeFormat.ETC2_RGB: return TextureFormat.ETC2_RGB;
                case AtoSafeFormat.ETC2_RGBA8: return TextureFormat.ETC2_RGBA8;
                case AtoSafeFormat.ASTC_4x4: return TextureFormat.ASTC_4x4;
                case AtoSafeFormat.ASTC_5x5: return TextureFormat.ASTC_5x5;
                case AtoSafeFormat.ASTC_6x6: return TextureFormat.ASTC_6x6;
                case AtoSafeFormat.ASTC_8x8: return TextureFormat.ASTC_8x8;
                case AtoSafeFormat.PVRTC_RGB4: return TextureFormat.PVRTC_RGB4;
                case AtoSafeFormat.PVRTC_RGBA4: return TextureFormat.PVRTC_RGBA4;
                default: return TextureFormat.RGBA32;
            }
        }
    }
}
