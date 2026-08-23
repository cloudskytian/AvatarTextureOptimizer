// -----------------------------------------------------------------------------
// ATOPlatform.cs — platform detection, format choice resolution & safety fallback.
// ATOPlatform.cs —— 平台检测、格式选择解析与安全兜底。
//
// Safety rules (per spec): alpha textures never get alpha-less formats; single-channel
// formats are refused for multi-channel gray masks (fallback + warning); PVRTC only on
// iOS with POT atlases; anything unsupported by EditorUtility.CompressTexture falls back.
// 安全规则（按规格）：含 alpha 贴图不给无 alpha 格式；多通道灰度贴图拒绝单通道格式
// （兜底+警告）；PVRTC 仅 iOS 且 POT 图集；压缩失败一律兜底。
// -----------------------------------------------------------------------------

using System;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class ATOPlatform
    {
        /// <summary>Detect the build platform / 检测构建平台。</summary>
        public static net.fosa.ato.ATOPlatform Detect()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return net.fosa.ato.ATOPlatform.Android;
                case BuildTarget.iOS: return net.fosa.ato.ATOPlatform.iOS;
                default: return net.fosa.ato.ATOPlatform.PC;
            }
        }

        /// <summary>Resolve a user format choice for one texture class on one platform.
        /// Auto picks the best safe default. Returns (format, note).
        /// 解析某平台某贴图类别的格式选择；Auto 时给最优安全默认。返回（格式, 说明）。</summary>
        public static (TextureFormat format, string note) Resolve(ATOFormat choice,
            TexClass cls, net.fosa.ato.ATOPlatform platform, bool atlasIsPOT)
        {
            var f = choice;

            // Platform validity / 平台有效性
            if (platform != net.fosa.ato.ATOPlatform.PC &&
                (f == ATOFormat.DXT1 || f == ATOFormat.DXT5 || f == ATOFormat.BC7 ||
                 f == ATOFormat.DXT1Crunched || f == ATOFormat.DXT5Crunched || f == ATOFormat.BC5))
                f = ATOFormat.Auto;
            if (platform == net.fosa.ato.ATOPlatform.iOS &&
                (f == ATOFormat.ETC2_RGB || f == ATOFormat.ETC2_RGBA8 || f == ATOFormat.ETC2RGBA8Crunched))
                f = ATOFormat.Auto; // Apple GPUs have no ETC2 / Apple GPU 无 ETC2
            if (platform != net.fosa.ato.ATOPlatform.iOS &&
                (f == ATOFormat.PVRTC4RGB || f == ATOFormat.PVRTC4RGBA))
                f = ATOFormat.Auto;
            if (platform == net.fosa.ato.ATOPlatform.iOS && !atlasIsPOT &&
                (f == ATOFormat.PVRTC4RGB || f == ATOFormat.PVRTC4RGBA))
                f = ATOFormat.Auto; // NPOT atlas cannot use PVRTC / NPOT 图集不能用 PVRTC

            if (f != ATOFormat.Auto)
            {
                var (ok, why) = IsSafe(f, cls, platform);
                if (!ok) return Resolve(ATOFormat.Auto, cls, platform, atlasIsPOT);
                return (Map(f), $"user choice {f}");
            }

            // Auto defaults / 自动默认
            switch (platform)
            {
                case net.fosa.ato.ATOPlatform.Android:
                case net.fosa.ato.ATOPlatform.iOS:
                    switch (cls)
                    {
                        case TexClass.NormalMap: return (TextureFormat.ASTC4x4, "auto mobile normal");
                        case TexClass.GrayMask: return (TextureFormat.ASTC6x6, "auto mobile gray");
                        case TexClass.AlbedoAlpha: return (TextureFormat.ASTC6x6, "auto mobile alpha");
                        default: return (TextureFormat.ASTC6x6, "auto mobile opaque");
                    }
                default:
                    switch (cls)
                    {
                        case TexClass.NormalMap: return (TextureFormat.DXT5, "auto pc normal (DXTnm)");
                        case TexClass.GrayMask: return (TextureFormat.DXT5, "auto pc gray");
                        case TexClass.AlbedoAlpha: return (TextureFormat.DXT5, "auto pc alpha");
                        default: return (TextureFormat.DXT1, "auto pc opaque");
                    }
            }
        }

        private static (bool, string) IsSafe(ATOFormat f, TexClass cls, net.fosa.ato.ATOPlatform p)
        {
            // Alpha must exist for alpha classes / alpha 类必须有 alpha 通道
            if (cls == TexClass.AlbedoAlpha && (f == ATOFormat.DXT1 || f == ATOFormat.DXT1Crunched ||
                                                f == ATOFormat.ETC2_RGB || f == ATOFormat.PVRTC4RGB))
                return (false, "alpha texture needs an alpha format / 含alpha贴图需alpha格式");
            // Normals need alpha for DXTnm on PC, or use BC5 / 法线在PC需alpha或BC5
            if (cls == TexClass.NormalMap && p == net.fosa.ato.ATOPlatform.PC &&
                (f == ATOFormat.DXT1 || f == ATOFormat.DXT1Crunched))
                return (false, "normal map cannot use DXT1 / 法线不能用DXT1");
            return (true, null);
        }

        internal static TextureFormat Map(ATOFormat f)
        {
            switch (f)
            {
                case ATOFormat.DXT1: return TextureFormat.DXT1;
                case ATOFormat.DXT5: return TextureFormat.DXT5;
                case ATOFormat.BC7: return TextureFormat.BC7;
                case ATOFormat.DXT1Crunched: return TextureFormat.DXT1Crunched;
                case ATOFormat.DXT5Crunched: return TextureFormat.DXT5Crunched;
                case ATOFormat.BC5: return TextureFormat.BC5;
                case ATOFormat.ASTC4x4: return TextureFormat.ASTC_4x4;
                case ATOFormat.ASTC5x5: return TextureFormat.ASTC_5x5;
                case ATOFormat.ASTC6x6: return TextureFormat.ASTC_6x6;
                case ATOFormat.ASTC8x8: return TextureFormat.ASTC_8x8;
                case ATOFormat.ETC2_RGB: return TextureFormat.ETC2_RGB;
                case ATOFormat.ETC2_RGBA8: return TextureFormat.ETC2_RGBA8;
                case ATOFormat.ETC2RGBA8Crunched: return TextureFormat.ETC2_RGBA8Crunched;
                case ATOFormat.PVRTC4RGB: return TextureFormat.PVRTC_RGB4;
                case ATOFormat.PVRTC4RGBA: return TextureFormat.PVRTC_RGBA4;
                default: return TextureFormat.RGBA32;
            }
        }

        /// <summary>Choose format set for the active platform (override-aware).
        /// 按当前平台选择格式集（含 override）。</summary>
        public static ATOFormatSet EffectiveFormats(ATOBuildState st)
        {
            var c = st.settings.component;
            var ov = c.GetOverride(st.settings.platform);
            if (ov.enabled) return ov.formats;
            return new ATOFormatSet(); // Auto everywhere / 全 Auto
        }

        /// <summary>Is the platform's normal convention DXTnm (AG channels)?
        /// 该平台法线是否为 DXTnm（AG 通道）约定？</summary>
        public static bool UsesDxtNm(net.fosa.ato.ATOPlatform p) => p == net.fosa.ato.ATOPlatform.PC;
    }
}
