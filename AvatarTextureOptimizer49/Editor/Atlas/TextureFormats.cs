using System;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Platform-aware format resolution with safety fallbacks. Auto picks sensible defaults per
    /// category+platform; unsafe user choices (e.g. no-alpha format on an alpha atlas, crunch on
    /// mobile, formats unavailable for the platform) fall back with a console warning — any option
    /// combination must never produce a broken material. / 平台感知的格式解析与安全回退：
    /// 任意选项组合都不会产出错误材质；不安全选择自动回退并警告。
    /// </summary>
    internal static class TextureFormats
    {
        /// <summary>Resolve the concrete TextureFormat for a generated texture. / 解析具体纹理格式。</summary>
        internal static TextureFormat Resolve(AtoFormat user, TextureCategory cat, AtoPlatform platform,
            bool hasAlpha, bool npot, bool singleChannelContent, out string warning)
        {
            warning = null;
            var f = user;

            // ---- normal maps: restricted set (canonical rg-xyz layout compatible) / 法线仅限安全集合 ----
            if (cat == TextureCategory.Normal)
            {
                if (f == AtoFormat.Auto) f = platform == AtoPlatform.PC ? AtoFormat.BC5 : AtoFormat.ASTC_4x4;
                if (platform != AtoPlatform.PC && (f == AtoFormat.BC5 || f == AtoFormat.BC4 || f == AtoFormat.BC7 ||
                                                   f == AtoFormat.DXT1 || f == AtoFormat.DXT5 ||
                                                   f == AtoFormat.CrunchDXT1 || f == AtoFormat.CrunchDXT5))
                {
                    warning = $"normal format {f} not available on {platform}; fell back to ASTC 4x4 / 法线格式回退";
                    f = AtoFormat.ASTC_4x4;
                }
                if (platform == AtoPlatform.PC && f is AtoFormat.ASTC_4x4 or AtoFormat.ASTC_5x5 or AtoFormat.ASTC_6x6
                        or AtoFormat.ASTC_8x8 or AtoFormat.ASTC_10x10 or AtoFormat.ASTC_12x12 or AtoFormat.ETC2_RGBA8
                        or AtoFormat.ETC2_RGB)
                {
                    warning = $"normal format {f} not available on PC; fell back to BC5 / 法线格式回退";
                    f = AtoFormat.BC5;
                }
                if (f != AtoFormat.BC5 && f != AtoFormat.ASTC_4x4 && f != AtoFormat.ASTC_5x5 &&
                    f != AtoFormat.ASTC_6x6 && f != AtoFormat.ASTC_8x8 && f != AtoFormat.Uncompressed)
                {
                    warning = $"normal format {f} unsafe; fell back / 法线格式不安全，回退";
                    f = platform == AtoPlatform.PC ? AtoFormat.BC5 : AtoFormat.ASTC_4x4;
                }
                return ToTextureFormat(f, npot, ref warning);
            }

            // ---- color / gray / mask categories / 颜色、灰度、蒙版 ----
            if (f == AtoFormat.Auto)
            {
                f = (cat, platform, singleChannelContent) switch
                {
                    (TextureCategory.Opaque, AtoPlatform.PC, false) => AtoFormat.DXT1,
                    (TextureCategory.Opaque, AtoPlatform.PC, true) => AtoFormat.BC4,
                    (TextureCategory.Transparent, AtoPlatform.PC, _) => AtoFormat.DXT5,
                    (TextureCategory.Grayscale, AtoPlatform.PC, true) => AtoFormat.BC4,
                    (TextureCategory.Grayscale, AtoPlatform.PC, false) => AtoFormat.DXT1,
                    (TextureCategory.Opaque, _, _) => AtoFormat.ASTC_6x6,
                    (TextureCategory.Transparent, _, _) => AtoFormat.ASTC_6x6,
                    (TextureCategory.Grayscale, _, _) => AtoFormat.ASTC_4x4,
                    _ => AtoFormat.ASTC_6x6,
                };
            }

            // crunch is PC-only / Crunch 仅PC
            if ((f == AtoFormat.CrunchDXT1 || f == AtoFormat.CrunchDXT5) && platform != AtoPlatform.PC)
            {
                warning = $"crunch is not available on {platform}; fell back to ASTC 6x6 / Crunch回退";
                f = AtoFormat.ASTC_6x6;
            }

            // BC/DXT on mobile → ASTC / 移动端BC回退
            if (platform != AtoPlatform.PC && (f == AtoFormat.BC7 || f == AtoFormat.DXT1 || f == AtoFormat.DXT5 ||
                                               f == AtoFormat.BC4 || f == AtoFormat.CrunchDXT1 ||
                                               f == AtoFormat.CrunchDXT5))
            {
                warning = $"{f} not available on {platform}; fell back to ASTC 6x6 / 格式回退";
                f = AtoFormat.ASTC_6x6;
            }

            // ASTC/ETC2 on PC → DXT / PC端ASTC回退
            if (platform == AtoPlatform.PC && (f == AtoFormat.ASTC_4x4 || f == AtoFormat.ASTC_5x5 ||
                                               f == AtoFormat.ASTC_6x6 || f == AtoFormat.ASTC_8x8 ||
                                               f == AtoFormat.ASTC_10x10 || f == AtoFormat.ASTC_12x12 ||
                                               f == AtoFormat.ETC2_RGBA8 || f == AtoFormat.ETC2_RGB))
            {
                warning = $"{f} not available on PC; fell back to DXT / 格式回退";
                f = hasAlpha ? AtoFormat.DXT5 : AtoFormat.DXT1;
            }

            // alpha texture must use an alpha-capable format / 含alpha必须使用带alpha格式
            if (hasAlpha && (f == AtoFormat.DXT1 || f == AtoFormat.CrunchDXT1 || f == AtoFormat.BC4 ||
                             f == AtoFormat.ETC2_RGB))
            {
                warning = $"alpha content with no-alpha format {f}; fell back to alpha format / alpha回退";
                f = platform == AtoPlatform.PC ? AtoFormat.DXT5 : AtoFormat.ASTC_6x6;
            }

            // multi-channel grayscale content with a single-channel format: keep multi-channel
            // storage + warn (per spec) / 多通道内容配单通道格式：仍按多通道保存并警告
            if (!singleChannelContent && f == AtoFormat.BC4 && cat == TextureCategory.Grayscale)
            {
                warning = "grayscale texture uses multiple channels; saved as multi-channel / 灰度多通道，按多通道保存";
                f = platform == AtoPlatform.PC ? AtoFormat.DXT1 : AtoFormat.ASTC_6x6;
            }

            return ToTextureFormat(f, npot, ref warning);
        }

        private static TextureFormat ToTextureFormat(AtoFormat f, bool npot, ref string warning)
        {
            switch (f)
            {
                case AtoFormat.BC7: return TextureFormat.BC7;
                case AtoFormat.DXT1: return TextureFormat.DXT1;
                case AtoFormat.DXT5: return TextureFormat.DXT5;
                case AtoFormat.BC5: return TextureFormat.BC5;
                case AtoFormat.BC4: return TextureFormat.BC4;
                case AtoFormat.ASTC_4x4: return TextureFormat.ASTC_4x4;
                case AtoFormat.ASTC_5x5: return TextureFormat.ASTC_5x5;
                case AtoFormat.ASTC_6x6: return TextureFormat.ASTC_6x6;
                case AtoFormat.ASTC_8x8: return TextureFormat.ASTC_8x8;
                case AtoFormat.ASTC_10x10: return TextureFormat.ASTC_10x10;
                case AtoFormat.ASTC_12x12: return TextureFormat.ASTC_12x12;
                case AtoFormat.ETC2_RGBA8: return TextureFormat.ETC2_RGBA8;
                case AtoFormat.ETC2_RGB: return TextureFormat.ETC2_RGB;
                case AtoFormat.Uncompressed: return TextureFormat.RGBA32;
                case AtoFormat.CrunchDXT1:
                case AtoFormat.CrunchDXT5:
                    if (npot)
                    {
                        warning = (warning ?? "") + " crunch with NPOT may fail; will auto-fallback / NPOT+Crunch可能失败，自动回退";
                    }
                    return f == AtoFormat.CrunchDXT1 ? TextureFormat.DXT1Crunched : TextureFormat.DXT5Crunched;
                default: return TextureFormat.DXT5;
            }
        }

        /// <summary>Create, compress, and configure a generated texture. / 创建、压缩并配置生成贴图。</summary>
        internal static Texture2D BuildTexture(string name, int w, int h, Color32[] pixels,
            TextureFormat format, bool srgb, bool mipAndStreaming, AtoPlatform platform)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipAndStreaming, !srgb)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,      // forced / 强制Clamp
                filterMode = FilterMode.Bilinear,
                anisoLevel = 4,
            };
            tex.SetPixels32(pixels);
            tex.Apply(mipAndStreaming, false);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                EditorUtility.CompressTexture(tex, format, TextureCompressionQuality.Best);
            }
            catch (Exception e)
            {
                ATOLog.Warning($"compress {name} to {format} failed ({e.Message}); fallback DXT5/ASTC / 压缩失败回退");
                var fallback = platform == AtoPlatform.PC ? TextureFormat.DXT5 : TextureFormat.ASTC_6x6;
                if (format == fallback) fallback = TextureFormat.RGBA32;
                EditorUtility.CompressTexture(tex, fallback, TextureCompressionQuality.Best);
            }

            if (mipAndStreaming)
            {
                // MipStreaming is bound to mipmap generation / Mip与Streaming绑定
                SetStreamingMipmaps(tex, true);
            }

            ATOLog.Info($"texture '{name}': {w}x{h} → {tex.format}, mip={mipAndStreaming}, " +
                        $"{sw.ElapsedMilliseconds}ms");
            return tex;
        }

        /// <summary>Set the serialized m_StreamingMipmaps flag (editor-only property). / 设置序列化MipStreaming标志。</summary>
        internal static void SetStreamingMipmaps(Texture2D tex, bool on)
        {
            try
            {
                var so = new SerializedObject(tex);
                var sp = so.FindProperty("m_StreamingMipmaps");
                if (sp != null)
                {
                    sp.intValue = on ? 1 : 0;
                    var prio = so.FindProperty("m_StreamingMipmapsPriority");
                    if (prio != null) prio.intValue = 0;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            catch (Exception e)
            {
                ATOLog.Warning($"streaming mipmaps flag failed on {tex.name}: {e.Message}");
            }
        }

        /// <summary>Estimated bytes per pixel for a format (reporting only). / 估算每像素字节数（报告用）。</summary>
        internal static float BytesPerPixel(TextureFormat f) => f switch
        {
            TextureFormat.DXT1 => 0.5f,
            TextureFormat.DXT1Crunched => 0.35f,
            TextureFormat.BC4 => 0.5f,
            TextureFormat.BC5 => 1f,
            TextureFormat.DXT5 => 1f,
            TextureFormat.DXT5Crunched => 0.7f,
            TextureFormat.BC7 => 1f,
            TextureFormat.ASTC_4x4 => 1f,
            TextureFormat.ASTC_5x5 => 0.64f,
            TextureFormat.ASTC_6x6 => 0.4444f,
            TextureFormat.ASTC_8x8 => 0.25f,
            TextureFormat.ASTC_10x10 => 0.16f,
            TextureFormat.ASTC_12x12 => 0.1111f,
            TextureFormat.ETC2_RGBA8 => 1f,
            TextureFormat.ETC2_RGB => 0.5f,
            TextureFormat.RGBA32 => 4f,
            _ => 1f,
        };
    }
}
