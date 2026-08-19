// CompressionApplier.cs
// Applies safe compression formats to generated textures with build-time fallbacks:
// category (alpha/opaque/normal/gray) × platform, mip-streaming binding, clamp, no-R/W.
// 对生成贴图应用安全压缩格式(含构建时兜底):类别(透明/不透明/法线/灰度)×平台、
// Mip与Streaming绑定、Clamp、关闭Read/Write。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato
{
    internal sealed partial class ATOProcessor
    {
        private enum TexCategory
        {
            Opaque,
            Alpha,
            Normal,
            Gray,
        }

        private void ApplyCompression()
        {
            int done = 0;
            var generated = CollectGeneratedTextures();
            foreach (var kv in generated)
            {
                Tick($"ATO: compressing ({done}/{generated.Count})", 0.9f + 0.08f * done / Mathf.Max(1, generated.Count));
                done++;
                CompressOne(kv.Key, kv.Value);
            }
            ATOLog.Info($"compression applied to {generated.Count} generated textures");
        }

        private Dictionary<Texture2D, TexCategory> CollectGeneratedTextures()
        {
            var map = new Dictionary<Texture2D, TexCategory>();
            foreach (var plan in _d.AtlasPlans)
            {
                if (plan.Baked == null) continue;
                var cat = Categorize(plan.Role, plan.HasAlpha);
                map[plan.Baked] = cat;
            }
            foreach (var kv in _d.StandaloneBaked)
            {
                TextureNode node;
                bool hasAlpha = true;
                if (_d.TextureNodes.TryGetValue(kv.Key, out node))
                    hasAlpha = NodeHasAlphaUsage(node);
                map[kv.Value] = Categorize(node != null ? node.PrimaryRole : TexRole.Color, hasAlpha);
            }
            return map;
        }

        private bool NodeHasAlphaUsage(TextureNode node)
        {
            foreach (var u in node.Usages)
                if (u.Alpha != AlphaMode.Opaque) return true;
            return false;
        }

        private static TexCategory Categorize(TexRole role, bool hasAlpha)
        {
            if (role == TexRole.Normal) return TexCategory.Normal;
            if (role == TexRole.Mask) return TexCategory.Gray;
            return hasAlpha ? TexCategory.Alpha : TexCategory.Opaque;
        }

        private void CompressOne(Texture2D tex, TexCategory cat)
        {
            var p = _d.EffectiveProfile;
            var choice = cat == TexCategory.Opaque ? p.opaqueFormat
                : cat == TexCategory.Alpha ? p.alphaFormat
                : cat == TexCategory.Normal ? p.normalFormat
                : p.grayFormat;

            bool multiChannel = MultiChannelContent(tex, cat);
            var format = ResolveFormat(choice, cat, multiChannel, tex);
            bool mipStreaming = cat == TexCategory.Opaque ? p.mipStreamingOpaque
                : cat == TexCategory.Alpha ? p.mipStreamingAlpha
                : cat == TexCategory.Normal ? p.mipStreamingNormal
                : p.mipStreamingGray;

            try
            {
                int quality = IsCrunch(format) ? 80 : 100;
                EditorUtility.CompressTexture(tex, format, quality);
            }
            catch (Exception e)
            {
                ATOLog.Warn($"compress failed for '{tex.name}' → {format}: {e.Message}; trying BC7 fallback");
                try { EditorUtility.CompressTexture(tex, TextureFormat.BC7, 100); }
                catch { ATOLog.Warn($"BC7 fallback also failed for '{tex.name}'; leaving RGBA32"); }
            }

            tex.wrapMode = TextureWrapMode.Clamp; // forced / 强制

            // Bind mips & streaming (VRChat rule: mips require streaming). / 绑定 Mip 与 Streaming(VRC 规则)。
            try
            {
                tex.Apply(true, mipStreaming ? true : false);
            }
            catch (Exception e)
            {
                ATOLog.V($"Apply(mip) failed for {tex.name}: {e.Message}");
            }

            if (mipStreaming) SetStreamingViaSerialized(tex);
            MakeNoLongerReadable(tex);
        }

        /// <summary>Gray category: does the content actually use multiple channels? / 灰度类:内容是否实际多通道。</summary>
        private bool MultiChannelContent(Texture2D tex, TexCategory cat)
        {
            if (cat != TexCategory.Gray) return false;
            // look up channel usage from the analysis graph / 从分析图查通道使用
            foreach (var kv in _d.AtlasByTexture)
            {
                if (kv.Value.Baked != tex) continue;
                TextureNode node;
                if (_d.TextureNodes.TryGetValue(kv.Key, out node))
                {
                    foreach (var u in node.Usages)
                        if (u.UsedChannels != 0 && u.UsedChannels != 1) return true;
                }
            }
            return false;
        }

        private static bool IsCrunch(TextureFormat f) =>
            f == TextureFormat.DXT1Crunched || f == TextureFormat.DXT5Crunched;

        /// <summary>Resolve + clamp to a safe format for platform/content. / 解析并钳制为平台/内容安全的格式。</summary>
        private TextureFormat ResolveFormat(TexFormatChoice choice, TexCategory cat, bool multiChannel, Texture2D tex)
        {
            bool windows = _d.Platform == ATOPlatform.Windows;

            // Platform guard / 平台防护
            if (!windows && !IsAstc(choice))
            {
                ATOErrors.Report(_d.Ctx, ErrorSeverity.NonFatal, "ato.warn.format_platform_clamped", tex);
                choice = TexFormatChoice.Auto;
            }
            if (_d.EffectiveProfile.experimentalNpotAtlas && !windows)
            {
                // iOS excludes PVRTC (not offered anyway); ASTC fine with NPOT / iOS 剔除 PVRTC(本就不提供);ASTC 支持 NPOT
            }

            // Auto defaults / 自动默认
            if (choice == TexFormatChoice.Auto)
            {
                if (windows)
                    return cat == TexCategory.Normal ? TextureFormat.BC7
                        : cat == TexCategory.Gray ? (multiChannel ? TextureFormat.BC7 : TextureFormat.BC4)
                        : TextureFormat.BC7;
                return cat == TexCategory.Normal ? TextureFormat.ASTC_4x4 : TextureFormat.ASTC_6x6;
            }

            // Content guards / 内容防护
            if (cat == TexCategory.Alpha && (choice == TexFormatChoice.DXT1 || choice == TexFormatChoice.DXT1Crunched))
            {
                ATOErrors.Report(_d.Ctx, ErrorSeverity.NonFatal, "ato.warn.format_alpha_clamped", tex);
                return windows ? TextureFormat.BC7 : TextureFormat.ASTC_6x6;
            }
            if (cat == TexCategory.Gray && choice == TexFormatChoice.BC4 && multiChannel)
            {
                ATOErrors.Report(_d.Ctx, ErrorSeverity.NonFatal, "ato.warn.format_gray_clamped", tex);
                return windows ? TextureFormat.BC7 : TextureFormat.ASTC_6x6;
            }

            switch (choice)
            {
                case TexFormatChoice.BC7: return TextureFormat.BC7;
                case TexFormatChoice.DXT1: return TextureFormat.DXT1;
                case TexFormatChoice.DXT1Crunched: return TextureFormat.DXT1Crunched;
                case TexFormatChoice.DXT5: return TextureFormat.DXT5;
                case TexFormatChoice.DXT5Crunched: return TextureFormat.DXT5Crunched;
                case TexFormatChoice.BC4: return TextureFormat.BC4;
                case TexFormatChoice.ASTC4x4: return TextureFormat.ASTC_4x4;
                case TexFormatChoice.ASTC6x6: return TextureFormat.ASTC_6x6;
                case TexFormatChoice.ASTC8x8: return TextureFormat.ASTC_8x8;
                default: return windows ? TextureFormat.BC7 : TextureFormat.ASTC_6x6;
            }
        }

        private static bool IsAstc(TexFormatChoice c) =>
            c == TexFormatChoice.ASTC4x4 || c == TexFormatChoice.ASTC6x6 || c == TexFormatChoice.ASTC8x8;

        private static void SetStreamingViaSerialized(Texture2D tex)
        {
            try
            {
                var so = new SerializedObject(tex);
                var prop = so.FindProperty("m_StreamingMipmaps");
                if (prop != null)
                {
                    prop.boolValue = true;
                    var prio = so.FindProperty("m_StreamingMipmapsPriority");
                    if (prio != null) prio.intValue = 0;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                else
                {
                    ATOLog.V($"m_StreamingMipmaps not found on '{tex.name}' (older Unity?)");
                }
            }
            catch (Exception e)
            {
                ATOLog.V($"streaming flag failed for '{tex.name}': {e.Message}");
            }
        }

        private static void MakeNoLongerReadable(Texture2D tex)
        {
            try
            {
                tex.Apply(false, true); // makeNoLongerReadable / 关闭可读
            }
            catch (Exception e)
            {
                ATOLog.V($"make-no-longer-readable failed for '{tex.name}': {e.Message}");
            }
        }
    }
}
