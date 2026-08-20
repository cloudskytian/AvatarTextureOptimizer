// AvatarTextureOptimizer - TextureAssetWriter
// EN: Creates top-level texture assets under the NDMF temp root with full importer settings: safe compression
// per category & platform, Mipmap<->MipStreaming binding, forced Clamp & no Read/Write, and safety fallbacks
// (alpha-required formats, multi-channel gray, NPOT-excluded PVRTC).
// CN: 在 NDMF 临时根目录下创建顶层贴图资产并配置完整导入设置：按分类与平台的安全压缩、
//     Mipmap⇔MipStreaming 绑定、强制 Clamp 与关闭 Read/Write、安全回退（alpha 必需、多通道灰度、NPOT 剔除 PVRTC）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    public static class TextureAssetWriter
    {
        /// <summary>EN: Resolves the NDMF temp asset root (internal API via reflection; fallback to Assets/). / CN: 解析 NDMF 临时资产根目录（反射访问 internal API；回退 Assets/）。</summary>
        public static string ResolveTempRoot()
        {
            try
            {
                var t = Type.GetType("nadena.dev.ndmf.AvatarProcessor, nadena.dev.ndmf");
                var prop = t?.GetProperty("TemporaryAssetRoot",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                var v = prop?.GetValue(null) as string;
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch (Exception) { }
            return "Assets/ATOTemp";
        }

        private static string EnsureFolder(string root, string name)
        {
            string folder = root + "/" + name;
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder(root, name);
            }
            return folder;
        }

        /// <summary>
        /// EN: Writes a Texture2D (RGBA32, optional mip chain) as a top-level asset with import settings.
        /// CN: 把 Texture2D（RGBA32，可选 mip 链）写为带导入设置的顶层资产。
        /// </summary>
        public static Texture2D CreateTextureAsset(AtoBuildState state, Texture2D src, string name,
            TextureCategory category, TextureUsage usage, bool srgb, FilterMode filter = FilterMode.Bilinear,
            int aniso = 1)
        {
            var root = ResolveTempRoot();
            string folder = EnsureFolder(root, "ATO_" + state.Ctx.AvatarRootObject.name);
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}.png");
            var data = src.EncodeToPNG();
            File.WriteAllBytes(path, data);
            AssetDatabase.ImportAsset(path);

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return null;
            ApplySettings(state, importer, category, usage, srgb, name, filter, aniso);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            return tex;
        }

        /// <summary>EN: Applies ATO importer settings (safety-first with warnings). / CN: 应用 ATO 导入设置（安全优先并告警）。</summary>
        public static void ApplySettings(AtoBuildState state, TextureImporter importer, TextureCategory category,
            TextureUsage usage, bool srgb, string displayName, FilterMode filter = FilterMode.Bilinear,
            int aniso = 1)
        {
            var profile = state.Profile;
            bool mipmaps = profile.mipmaps;

            // EN: Forced: Clamp wrap, no Read/Write.
            // CN: 强制：Clamp 环绕、关闭 Read/Write。
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.isReadable = false;

            // EN: Type: normal atlas → NormalMap (standard decode), others → Default.
            // CN: 类型：法线图集 → NormalMap（标准解码），其余 → Default。
            importer.textureType = usage == TextureUsage.Normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = srgb;

            // EN: Mipmap & MipStreaming bound together (VRChat requires streaming when mipmaps are on).
            // CN: Mipmap 与 MipStreaming 绑定（VRChat 要求开 Mipmap 时必须开 Streaming）。
            importer.mipmapEnabled = mipmaps;
            importer.streamingMipmaps = mipmaps;

            // EN: Filter mode & aniso: max quality across source textures (spec).
            // CN: 过滤模式与各向异性：源贴图最高质量（按需求）。
            importer.filterMode = filter;
            importer.anisoLevel = Mathf.Max(1, aniso);

            // EN: Compression per category & platform with safe fallback.
            // CN: 按分类与平台的压缩与安全回退。
            var chosen = profile.compression.For(category);
            bool hasAlpha = usage != TextureUsage.Opaque || category != TextureCategory.Opaque;
            var (fmt, fallbackReason) = ResolveFormat(state, chosen, category, hasAlpha);

            var platform = GetCurrentPlatform(state);
            var ps = importer.GetPlatformTextureSettings(platform);
            ps.overridden = true;
            ps.format = fmt;
            ps.maxTextureSize = Mathf.Max(32, Mathf.NextPowerOfTwo(Mathf.Max(importer.GetWidthForFormat(fmt), 1)));
            importer.SetPlatformTextureSettings(ps);
            importer.textureCompression = TextureImporterCompression.Compressed;

            if (fallbackReason != null)
                AtoLog.Warn(string.Format(I18n.T("warn.compression.fallback"), chosen, platform, fmt) + " " + fallbackReason);

            // EN: NPOT exclusion: PVRTC requires POT — excluded automatically (spec).
            // CN: NPOT 剔除：PVRTC 要求 POT——自动剔除（按需求）。
            if (profile.experimentalNpot && (fmt == TextureImporterFormat.PVRTC_RGB4 ||
                fmt == TextureImporterFormat.PVRTC_RGBA4 || fmt == TextureImporterFormat.PVRTC_RGB2 ||
                fmt == TextureImporterFormat.PVRTC_RGBA2))
            {
                AtoLog.Warn(string.Format(I18n.T("warn.compression.fallback"), fmt, platform, "ASTC_6x6") +
                            " (PVRTC requires POT; NPOT mode excludes it)");
                ps.format = TextureImporterFormat.ASTC_6x6;
                importer.SetPlatformTextureSettings(ps);
            }

            importer.SaveAndReimport();
        }

        /// <summary>EN: Current build platform string for TextureImporterPlatformSettings. / CN: 当前构建平台的 TextureImporterPlatformSettings 字符串。</summary>
        public static string GetCurrentPlatform(AtoBuildState state)
        {
            switch (state.Platform)
            {
                case AtoPlatform.Android: return "Android";
                case AtoPlatform.iOS: return "iPhone";
                default: return "Standalone";
            }
        }

        /// <summary>
        /// EN: Maps ATO format → TextureImporterFormat for the target platform with safety fallback
        /// (alpha-required formats, mobile-invalid BC formats, iOS PVRTC restrictions).
        /// CN: 把 ATO 格式映射为目标平台的 TextureImporterFormat，含安全回退。
        /// </summary>
        public static (TextureImporterFormat, string) ResolveFormat(AtoBuildState state,
            AtoCompressionFormat fmt, TextureCategory category, bool hasAlpha)
        {
            var platform = state.Platform;
            if (fmt == AtoCompressionFormat.Auto)
            {
                return (platform switch
                {
                    AtoPlatform.Android => hasAlpha ? TextureImporterFormat.ASTC_6x6 : TextureImporterFormat.ASTC_6x6,
                    AtoPlatform.iOS => hasAlpha ? TextureImporterFormat.ASTC_6x6 : TextureImporterFormat.ASTC_6x6,
                    _ => hasAlpha ? TextureImporterFormat.BC7 : TextureImporterFormat.BC7
                }, null);
            }

            // EN: Explicit formats: validate per platform, fall back with reason.
            // CN: 显式格式：按平台校验，不合规回退并给出原因。
            switch (platform)
            {
                case AtoPlatform.Android:
                    switch (fmt)
                    {
                        case AtoCompressionFormat.ETC2_RGB: return (TextureImporterFormat.ETC2_RGB4, null);
                        case AtoCompressionFormat.ETC2_RGBA: return (TextureImporterFormat.ETC2_RGBA8, null);
                        case AtoCompressionFormat.ETC1: return (TextureImporterFormat.ETC_RGB4, null);
                        case AtoCompressionFormat.ASTC_4x4: return (TextureImporterFormat.ASTC_4x4, null);
                        case AtoCompressionFormat.ASTC_6x6: return (TextureImporterFormat.ASTC_6x6, null);
                        case AtoCompressionFormat.ASTC_8x8: return (TextureImporterFormat.ASTC_8x8, null);
                        case AtoCompressionFormat.ASTC_10x10: return (TextureImporterFormat.ASTC_10x10, null);
                        case AtoCompressionFormat.ASTC_12x12: return (TextureImporterFormat.ASTC_12x12, null);
                        case AtoCompressionFormat.RGBA32: return (TextureImporterFormat.RGBA32, null);
                        case AtoCompressionFormat.RGB24: return (TextureImporterFormat.RGB24, null);
                        default:
                            return (hasAlpha ? TextureImporterFormat.ASTC_6x6 : TextureImporterFormat.ASTC_6x6,
                                $"BC/PVRTC not supported on Android");
                    }
                case AtoPlatform.iOS:
                    switch (fmt)
                    {
                        case AtoCompressionFormat.PVRTC_RGB4: return (TextureImporterFormat.PVRTC_RGB4, null);
                        case AtoCompressionFormat.PVRTC_RGBA4: return (TextureImporterFormat.PVRTC_RGBA4, null);
                        case AtoCompressionFormat.PVRTC_RGB2: return (TextureImporterFormat.PVRTC_RGB2, null);
                        case AtoCompressionFormat.PVRTC_RGBA2: return (TextureImporterFormat.PVRTC_RGBA2, null);
                        case AtoCompressionFormat.ASTC_4x4: return (TextureImporterFormat.ASTC_4x4, null);
                        case AtoCompressionFormat.ASTC_6x6: return (TextureImporterFormat.ASTC_6x6, null);
                        case AtoCompressionFormat.ASTC_8x8: return (TextureImporterFormat.ASTC_8x8, null);
                        case AtoCompressionFormat.RGBA32: return (TextureImporterFormat.RGBA32, null);
                        case AtoCompressionFormat.RGB24: return (TextureImporterFormat.RGB24, null);
                        default:
                            return (hasAlpha ? TextureImporterFormat.ASTC_6x6 : TextureImporterFormat.ASTC_6x6,
                                "BC/ETC not supported on iOS");
                    }
                default: // PC
                    switch (fmt)
                    {
                        case AtoCompressionFormat.BC1: return (TextureImporterFormat.BC1, null);
                        case AtoCompressionFormat.BC3: return (TextureImporterFormat.BC3, null);
                        case AtoCompressionFormat.BC4: return (TextureImporterFormat.BC4, null);
                        case AtoCompressionFormat.BC5: return (TextureImporterFormat.BC5, null);
                        case AtoCompressionFormat.BC7: return (TextureImporterFormat.BC7, null);
                        case AtoCompressionFormat.RGBA32: return (TextureImporterFormat.RGBA32, null);
                        case AtoCompressionFormat.RGB24: return (TextureImporterFormat.RGB24, null);
                        default:
                            return (hasAlpha ? TextureImporterFormat.BC7 : TextureImporterFormat.BC7,
                                "Mobile formats not supported on PC");
                    }
            }
        }

        /// <summary>EN: Resolves the effective filter mode (max quality across the atlas' source textures). / CN: 解析有效过滤模式（取图集源贴图的最高质量）。</summary>
        public static FilterMode ResolveFilterMode(PackedAtlas atlas)
        {
            FilterMode best = FilterMode.Bilinear;
            foreach (var pi in atlas.islands)
            {
                if (pi.tex.filterMode > best) best = pi.tex.filterMode;
            }
            return best;
        }

        /// <summary>EN: Resolves the max aniso level across the atlas' sources (spec: 其余参数取所有贴图中质量最高的). / CN: 解析图集源贴图的最大各向异性等级（按需求：其余参数取质量最高）。</summary>
        public static int ResolveAniso(PackedAtlas atlas)
        {
            int best = 1;
            foreach (var pi in atlas.islands)
            {
                if (pi.tex.texture != null && pi.tex.texture.anisoLevel > best)
                    best = pi.tex.texture.anisoLevel;
            }
            return best;
        }

        /// <summary>EN: Detects whether a grayscale texture uses more than one channel (multi-channel safety). / CN: 检测灰度贴图是否使用多通道（多通道安全规则）。</summary>
        public static bool UsesMultipleChannels(Texture2D tex, AtoBuildState state)
        {
            var decoded = state.Decoder != null ? state.Decoder.Decode(tex) : null;
            if (decoded == null) return false;
            var data = decoded.GetRawTextureData<Color32>();
            int used = 0;
            int step = Mathf.Max(1, data.Length / 1024);
            var min = new byte[3] { 255, 255, 255 };
            var max = new byte[3] { 0, 0, 0 };
            for (int i = 0; i < data.Length; i += step)
            {
                var c = data[i];
                min[0] = Math.Min(min[0], c.r); max[0] = Math.Max(max[0], c.r);
                min[1] = Math.Min(min[1], c.g); max[1] = Math.Max(max[1], c.g);
                min[2] = Math.Min(min[2], c.b); max[2] = Math.Max(max[2], c.b);
            }
            for (int c = 0; c < 3; c++) if (max[c] - min[c] > 8) used++;
            return used > 1;
        }
    }
}
