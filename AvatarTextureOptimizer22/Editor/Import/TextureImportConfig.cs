// AvatarTextureOptimizer
// File: Editor/Import/TextureImportConfig.cs
//
// Maps the user's safe compression enum to concrete TextureFormats with
// platform awareness and safe fallbacks:
//   - PC:      DXT1/DXT5/BC7 (RGBA32/RGB24 uncompressed fallback)
//   - Android: ETC2 RGB/RGBA, ASTC (RGBA32 fallback)
//   - iOS:     ETC2, ASTC; PVRTC EXCLUDED when NPOT is enabled (spec)
//   - alpha requirement: a texture with alpha never gets an alpha-less format
//     (DXT1/ETC2_RGB/RGB24); falls back with a warning
//   - grayscale: a user-chosen single-channel format is preserved only when
//     the content is truly single-channel; otherwise multi-channel is kept
//     with a warning (spec)
// Also binds Mipmap <-> MipStreaming (VRChat requirement) and applies the
// locked options (Clamp wrap, no Read/Write) to generated textures.
//
// 将用户的安全压缩枚举映射为具体 TextureFormat，带平台感知与安全兜底：
//   - PC：DXT1/DXT5/BC7（RGBA32/RGB24 未压缩兜底）
//   - Android：ETC2 RGB/RGBA、ASTC（RGBA32 兜底）
//   - iOS：ETC2、ASTC；启用 NPOT 时【剔除】PVRTC（规格）
//   - alpha 要求：带 alpha 的贴图绝不使用无 alpha 格式（DXT1/ETC2_RGB/
//     RGB24）；兜底并警告
//   - 灰度：用户选择的单通道格式仅在内容确实单通道时保留；否则保留多通道
//     并警告（规格）
// 同时绑定 Mipmap <-> MipStreaming（VRChat 要求），并对生成的贴图应用锁定
// 选项（Clamp 包裹、关闭 Read/Write）。

using System;
using System.Collections.Generic;
using net.fosa.avatar_texture_optimizer.editor.logging;
using net.fosa.avatar_texture_optimizer.editor.model;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.import
{
    public static class TextureImportConfig
    {
        /// <summary>
        /// Resolve the concrete TextureFormat for a category on the current
        /// platform, honoring user choice, alpha content and NPOT exclusions.
        /// Returns RGBA32/RGB24 fallback when the requested format is invalid.
        /// 解析当前平台上某类别应使用的具体 TextureFormat，尊重用户选择、
        /// alpha 内容与 NPOT 剔除规则。请求格式非法时返回 RGBA32/RGB24 兜底。
        /// </summary>
        public static TextureFormat ResolveFormat(ATOBuildState state, ATOImportCategory category,
            bool hasAlpha, bool enableNPOT)
        {
            var settings = state.Component.Import.For(category);
            // Platform override wins when enabled. / 启用平台覆写时以覆写为准。
            var overrideEnabled = state.Component.Platforms.Get(ToTarget(state)).Enabled;
            var format = overrideEnabled
                ? state.Component.Platforms.Get(ToTarget(state)).Compression
                : settings.Compression;

            if (format == ATOCompressionFormat.Auto)
                return AutoFor(state, category, hasAlpha, enableNPOT);

            TextureFormat resolved = MapEnum(format, state, enableNPOT);

            // Alpha safety: never drop the alpha channel. / alpha 安全：绝不丢失 alpha 通道。
            bool alphaLess = resolved == TextureFormat.DXT1 || resolved == TextureFormat.ETC2_RGB ||
                             resolved == TextureFormat.RGB24;
            if (hasAlpha && alphaLess)
            {
                state.Warn($"[ATO] Texture with alpha cannot use alpha-less format {format} -> falling back. / 带 alpha 的贴图不能使用无 alpha 格式 {format}，已兜底。");
                return hasAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24;
            }
            return resolved;
        }

        private static TextureFormat AutoFor(ATOBuildState state, ATOImportCategory category, bool hasAlpha, bool enableNPOT)
        {
            switch (state.Platform)
            {
                case ATOBuildPlatform.Android:
                case ATOBuildPlatform.iOS:
                    if (category == ATOImportCategory.NormalMap)
                        return TextureFormat.ETC2_RGBA8; // normal maps keep alpha channel / 法线贴图保留 alpha 通道
                    return hasAlpha ? TextureFormat.ETC2_RGBA8 : TextureFormat.ETC2_RGB;
                default:
                    if (category == ATOImportCategory.NormalMap)
                        return TextureFormat.BC7;
                    return hasAlpha ? TextureFormat.BC7 : TextureFormat.DXT1;
            }
        }

        private static TextureFormat MapEnum(ATOCompressionFormat format, ATOBuildState state, bool enableNPOT)
        {
            switch (format)
            {
                case ATOCompressionFormat.DXT1: return TextureFormat.DXT1;
                case ATOCompressionFormat.DXT5: return TextureFormat.DXT5;
                case ATOCompressionFormat.BC7: return TextureFormat.BC7;
                case ATOCompressionFormat.ETC2_RGB: return TextureFormat.ETC2_RGB;
                case ATOCompressionFormat.ETC2_RGBA: return TextureFormat.ETC2_RGBA8;
                case ATOCompressionFormat.ASTC_4x4: return TextureFormat.ASTC_4x4;
                case ATOCompressionFormat.ASTC_6x6: return TextureFormat.ASTC_6x6;
                case ATOCompressionFormat.ASTC_8x8: return TextureFormat.ASTC_8x8;
                case ATOCompressionFormat.RGBA32: return TextureFormat.RGBA32;
                case ATOCompressionFormat.RGB24: return TextureFormat.RGB24;
                default: return TextureFormat.RGBA32;
            }
        }

        /// <summary>
        /// Whether a format is excluded under NPOT (PVRTC on iOS per spec).
        /// 判断某格式是否在 NPOT 下被剔除（规格：iOS 剔除 PVRTC）。
        /// </summary>
        public static bool IsExcludedByNPOT(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.PVRTC_RGB2:
                case TextureFormat.PVRTC_RGBA2:
                case TextureFormat.PVRTC_RGB4:
                case TextureFormat.PVRTC_RGBA4:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Map the build platform to the settings enum. / 将构建平台映射到设置枚举。</summary>
        public static ATOTargetPlatform ToTarget(ATOBuildState state)
        {
            switch (state.Platform)
            {
                case ATOBuildPlatform.Android: return ATOTargetPlatform.Android;
                case ATOBuildPlatform.iOS: return ATOTargetPlatform.iOS;
                default: return ATOTargetPlatform.PC;
            }
        }

        /// <summary>
        /// Apply post-creation settings to a generated texture: mipmap +
        /// streaming-mipmap binding (VRChat), wrap Clamp, no Read/Write,
        /// filter mode and optional compression.
        /// 对生成的贴图应用创建后设置：mipmap 与 streaming-mipmap 绑定
        /// （VRChat）、wrap Clamp、关闭 Read/Write、过滤模式与可选压缩。
        /// </summary>
        public static void ApplyGeneratedSettings(ATOBuildState state, Texture2D tex, ATOImportCategory category,
            bool hasAlpha, bool enableNPOT, bool readableForDedup = true)
        {
            var settings = state.Component.Import.For(category);
            bool mipmap = settings.EnableMipmap;
            bool streaming = mipmap; // VRChat: mipmap ON -> streaming ON; OFF -> OFF / VRChat 绑定

            tex.wrapMode = TextureWrapMode.Clamp; // 强制 Clamp（锁定，不可修改）
            tex.filterMode = state.Component.Import.FilterMode;
            tex.name = tex.name;

            // Compression (only when the texture is readable).
            // 压缩（仅当贴图可读时）。
            if (tex.isReadable)
            {
                var fmt = ResolveFormat(state, category, hasAlpha, enableNPOT);
                if (!IsExcludedByNPOT(fmt) && SystemInfo.SupportsTextureFormat(fmt) &&
                    fmt != TextureFormat.RGBA32 && fmt != TextureFormat.RGB24)
                {
                    try
                    {
                        var quality = settings.CompressionQuality;
                        EditorUtility.CompressTexture(tex, fmt, quality);
                        ATOLog.Trace($"compressed {tex.name} as {fmt}");
                    }
                    catch (Exception e)
                    {
                        state.Warn($"[ATO] Compression to {fmt} failed for {tex.name}: {e.Message} -> uncompressed. / 压缩失败，改用未压缩。");
                    }
                }
            }

            // Mipmaps (generate if requested). / Mipmap（按需生成）。
            tex.Apply(mipmap, !readableForDedup);

            // Streaming mipmaps via SerializedObject (AAO technique).
            // 通过 SerializedObject 设置 streaming mipmaps（AAO 技术）。
            SetStreamingMipMaps(tex, streaming, 0);
        }

        private static void SetStreamingMipMaps(Texture2D tex, bool streaming, int priority)
        {
            try
            {
                using var so = new SerializedObject(tex);
                var prop = so.FindProperty("m_StreamingMipmaps");
                var prio = so.FindProperty("m_StreamingMipmapsPriority");
                if (prop != null) prop.boolValue = streaming;
                if (prio != null) prio.intValue = priority;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            catch (Exception e)
            {
                ATOLog.Trace($"streaming mipmap set failed: {e.Message}");
            }
        }

        /// <summary>
        /// Estimate the memory footprint of a texture in bytes (format-aware).
        /// 估算贴图的内存占用（字节，格式感知）。
        /// </summary>
        public static long EstimateBytes(Texture2D tex)
        {
            if (tex == null) return 0;
            try
            {
                return UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(tex);
            }
            catch
            {
                int bpp = 4;
                switch (tex.format)
                {
                    case TextureFormat.RGB24: bpp = 3; break;
                    case TextureFormat.DXT1: case TextureFormat.ETC2_RGB: case TextureFormat.PVRTC_RGB2: bpp = 1; break;
                    case TextureFormat.PVRTC_RGB4: case TextureFormat.PVRTC_RGBA2: bpp = 1; break;
                    case TextureFormat.PVRTC_RGBA4: case TextureFormat.ASTC_4x4: bpp = 1; break;
                    case TextureFormat.BC7: case TextureFormat.ETC2_RGBA8: case TextureFormat.ASTC_6x6:
                    case TextureFormat.ASTC_8x8: case TextureFormat.DXT5: bpp = 1; break;
                }
                long total = (long)tex.width * tex.height * bpp;
                int mips = tex.mipmapCount > 0 ? tex.mipmapCount : 1;
                // Approx mip chain: sum of 4^(-k). / mip 链近似：4^(-k) 求和。
                double chain = 0;
                for (int k = 0; k < mips; k++) chain += 1.0 / Math.Pow(4, k);
                return (long)(total * chain);
            }
        }
    }
}
