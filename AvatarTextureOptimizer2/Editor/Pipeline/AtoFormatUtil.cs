using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Platform-safe compression enum → TextureFormat, with fallbacks.
    /// 平台安全压缩格式映射与回退。
    /// </summary>
    public static class AtoFormatUtil
    {
        public static TextureFormat Resolve(
            AtoTextureRole role, bool hasAlpha, bool multiChannelGray,
            AtoPlatformOverride s, AtoPlatform platform, Texture2D tex, AtoReport report)
        {
            if (role == AtoTextureRole.Normal)
                return MapNormal(s.normalFormat, platform, tex, report);
            if (role == AtoTextureRole.Gray)
            {
                if (multiChannelGray && (s.grayFormat == AtoGrayFormat.R8 || s.grayFormat == AtoGrayFormat.BC4))
                {
                    report.Warn("warn.formatFallback", tex.name + " gray multi-channel → RGBA32");
                    return TextureFormat.RGBA32;
                }
                return MapGray(s.grayFormat, platform);
            }
            if (hasAlpha)
            {
                if (s.transparentFormat == AtoTransparentFormat.Auto)
                    return platform == AtoPlatform.PC ? TextureFormat.DXT5 : TextureFormat.ETC2_RGBA8;
                if (s.transparentFormat == AtoTransparentFormat.DXT5 && platform != AtoPlatform.PC)
                    return TextureFormat.ETC2_RGBA8;
                return MapTransparent(s.transparentFormat, platform);
            }
            if (s.opaqueFormat == AtoOpaqueFormat.Auto)
                return platform == AtoPlatform.PC ? TextureFormat.DXT1 : TextureFormat.ETC2_RGB;
            if (s.experimentalNpot && platform == AtoPlatform.iOS && s.opaqueFormat.ToString().Contains("PVRTC"))
            {
                report.Warn("warn.formatFallback", tex.name + " NPOT drop PVRTC");
                return TextureFormat.ASTC_6x6;
            }
            return MapOpaque(s.opaqueFormat, platform);
        }

        static TextureFormat MapOpaque(AtoOpaqueFormat f, AtoPlatform p) => f switch
        {
            AtoOpaqueFormat.DXT1 => p == AtoPlatform.PC ? TextureFormat.DXT1 : TextureFormat.ETC2_RGB,
            AtoOpaqueFormat.DXT5 => p == AtoPlatform.PC ? TextureFormat.DXT5 : TextureFormat.ETC2_RGBA8,
            AtoOpaqueFormat.BC7 => p == AtoPlatform.PC ? TextureFormat.BC7 : TextureFormat.ASTC_4x4,
            AtoOpaqueFormat.ASTC_6x6 => TextureFormat.ASTC_6x6,
            AtoOpaqueFormat.ASTC_4x4 => TextureFormat.ASTC_4x4,
            AtoOpaqueFormat.ETC2_RGB => TextureFormat.ETC2_RGB,
            AtoOpaqueFormat.RGBA32 => TextureFormat.RGBA32,
            _ => TextureFormat.RGBA32
        };

        static TextureFormat MapTransparent(AtoTransparentFormat f, AtoPlatform p) => f switch
        {
            AtoTransparentFormat.DXT5 => p == AtoPlatform.PC ? TextureFormat.DXT5 : TextureFormat.ETC2_RGBA8,
            AtoTransparentFormat.BC7 => p == AtoPlatform.PC ? TextureFormat.BC7 : TextureFormat.ASTC_4x4,
            AtoTransparentFormat.ASTC_6x6 => TextureFormat.ASTC_6x6,
            AtoTransparentFormat.ASTC_4x4 => TextureFormat.ASTC_4x4,
            AtoTransparentFormat.ETC2_RGBA8 => TextureFormat.ETC2_RGBA8,
            AtoTransparentFormat.RGBA32 => TextureFormat.RGBA32,
            _ => TextureFormat.RGBA32
        };

        static TextureFormat MapNormal(AtoNormalFormat f, AtoPlatform p, Texture2D tex, AtoReport report) => f switch
        {
            AtoNormalFormat.DXT5nm => p == AtoPlatform.PC ? TextureFormat.DXT5 : TextureFormat.ASTC_4x4,
            AtoNormalFormat.BC5 => p == AtoPlatform.PC ? TextureFormat.BC5 : TextureFormat.ASTC_4x4,
            AtoNormalFormat.ASTC_4x4 => TextureFormat.ASTC_4x4,
            AtoNormalFormat.RGBA32 => TextureFormat.RGBA32,
            _ => p == AtoPlatform.PC ? TextureFormat.DXT5 : TextureFormat.ASTC_4x4
        };

        static TextureFormat MapGray(AtoGrayFormat f, AtoPlatform p) => f switch
        {
            AtoGrayFormat.BC4 => p == AtoPlatform.PC ? TextureFormat.BC4 : TextureFormat.R8,
            AtoGrayFormat.DXT1 => p == AtoPlatform.PC ? TextureFormat.DXT1 : TextureFormat.ETC2_RGB,
            AtoGrayFormat.ASTC_6x6 => TextureFormat.ASTC_6x6,
            AtoGrayFormat.R8 => TextureFormat.R8,
            AtoGrayFormat.RGBA32 => TextureFormat.RGBA32,
            _ => TextureFormat.RGBA32
        };

        public static bool IsMultiChannelGray(Color32[] px)
        {
            for (int i = 0; i < px.Length; i += 16)
            {
                var c = px[i];
                if (c.g != c.r || c.b != c.r) return true;
            }
            return false;
        }
    }
}
