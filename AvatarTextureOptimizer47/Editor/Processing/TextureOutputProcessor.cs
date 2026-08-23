using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using Fosa.AvatarTextureOptimizer.Editor.Reporting;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Processing
{
    /// <summary>EN: Applies platform-safe compression, bound mip streaming, and read/write policy. ZH: 应用平台安全压缩、绑定的 Mip Streaming 与 Read/Write 策略。</summary>
    internal static class TextureOutputProcessor
    {
        public static void Apply(BuildPlan plan, BuildProgress progress, AtoBuildReport report)
        {
            var outputs = new Dictionary<Texture2D, TextureSemantic>();
            foreach (var layer in plan.GeneratedLayers) outputs[layer.Output] = layer.Semantic;
            foreach (var pair in plan.TextureReplacements)
            {
                if (outputs.ContainsKey(pair.Value)) continue;
                var semantics = plan.Materials.Values.SelectMany(x => x.Usages)
                    .Where(x => x.Texture == pair.Key).Select(x => x.Semantic);
                outputs[pair.Value] = Strictest(semantics);
            }

            var list = outputs.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                progress.Report("Applying texture output settings / 应用贴图输出设置", i, Math.Max(1, list.Count));
                var texture = list[i].Key; var semantic = list[i].Value;
                var settings = plan.Profile.ForSemantic(semantic);
                var requested = ValidateFormat(settings.compression, semantic, plan.Platform,
                    plan.Profile.experimentalNpotAtlases, texture, report);
                var format = ResolveFormat(requested, semantic, plan.Platform);
                if (format.HasValue)
                {
                    try { EditorUtility.CompressTexture(texture, format.Value, TextureCompressionQuality.Best); }
                    catch (Exception ex) { report.Warn($"Compression {format.Value} failed for '{texture.name}': {ex.Message}; RGBA fallback retained.", texture); }
                }

                using (var serialized = new SerializedObject(texture))
                {
                    var streaming = serialized.FindProperty("m_StreamingMipmaps");
                    if (streaming != null)
                    {
                        streaming.boolValue = settings.mipmapsAndStreaming && texture.mipmapCount > 1;
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
                try { texture.Apply(false, true); }
                catch (Exception ex) { report.Warn($"Could not close Read/Write for '{texture.name}': {ex.Message}", texture); }
            }
        }

        private static SafeTextureFormat ValidateFormat(SafeTextureFormat requested, TextureSemantic semantic,
            OptimizerPlatform platform, bool npot, Texture2D texture, AtoBuildReport report)
        {
            if (requested == SafeTextureFormat.Automatic || requested == SafeTextureFormat.UncompressedRGBA32) return requested;
            if (semantic == TextureSemantic.Normal && requested != SafeTextureFormat.BC5 && requested != SafeTextureFormat.BC7 &&
                requested != SafeTextureFormat.ASTC4x4 && requested != SafeTextureFormat.ASTC6x6 && requested != SafeTextureFormat.ASTC8x8)
            {
                report.Warn($"{requested} is not a safe normal-map layout for '{texture.name}'; Automatic was selected.", texture);
                return SafeTextureFormat.Automatic;
            }
            var alpha = semantic == TextureSemantic.ColorAlpha;
            if (alpha && (requested == SafeTextureFormat.BC1 || requested == SafeTextureFormat.ETC2RGB ||
                          requested == SafeTextureFormat.PVRTCRGB4 || requested == SafeTextureFormat.DXT1Crunched ||
                          requested == SafeTextureFormat.ETC1Crunched))
            {
                report.Warn($"'{texture.name}' has alpha; alpha-less {requested} was replaced by Automatic.", texture);
                return SafeTextureFormat.Automatic;
            }
            if (npot && (requested == SafeTextureFormat.PVRTCRGB4 || requested == SafeTextureFormat.PVRTCRGBA4))
            {
                report.Warn($"PVRTC does not support this NPOT output; '{texture.name}' uses Automatic.", texture);
                return SafeTextureFormat.Automatic;
            }
            if (platform == OptimizerPlatform.PC && IsMobile(requested) ||
                (platform == OptimizerPlatform.Android || platform == OptimizerPlatform.IOS) && IsDesktop(requested))
            {
                report.Warn($"{requested} is unavailable on {platform}; '{texture.name}' uses Automatic.", texture);
                return SafeTextureFormat.Automatic;
            }
            if ((platform == OptimizerPlatform.Android && (requested == SafeTextureFormat.PVRTCRGB4 || requested == SafeTextureFormat.PVRTCRGBA4)) ||
                (platform == OptimizerPlatform.IOS && IsEtc(requested)))
            {
                report.Warn($"{requested} is not supported by {platform}; '{texture.name}' uses Automatic.", texture);
                return SafeTextureFormat.Automatic;
            }
            return requested;
        }

        private static TextureFormat? ResolveFormat(SafeTextureFormat format, TextureSemantic semantic, OptimizerPlatform platform)
        {
            if (format == SafeTextureFormat.UncompressedRGBA32) return null;
            if (format == SafeTextureFormat.Automatic)
            {
                if (platform == OptimizerPlatform.PC)
                {
                    if (semantic == TextureSemantic.Normal) return TextureFormat.BC5;
                    return TextureFormat.BC7;
                }
                if (semantic == TextureSemantic.Normal || semantic == TextureSemantic.ColorAlpha) return TextureFormat.ASTC_4x4;
                return TextureFormat.ASTC_6x6;
            }
            switch (format)
            {
                case SafeTextureFormat.BC1: return TextureFormat.DXT1;
                case SafeTextureFormat.BC3: return TextureFormat.DXT5;
                case SafeTextureFormat.BC5: return TextureFormat.BC5;
                case SafeTextureFormat.BC7: return TextureFormat.BC7;
                case SafeTextureFormat.ASTC4x4: return TextureFormat.ASTC_4x4;
                case SafeTextureFormat.ASTC6x6: return TextureFormat.ASTC_6x6;
                case SafeTextureFormat.ASTC8x8: return TextureFormat.ASTC_8x8;
                case SafeTextureFormat.ETC2RGB: return TextureFormat.ETC2_RGB;
                case SafeTextureFormat.ETC2RGBA8: return TextureFormat.ETC2_RGBA8;
                case SafeTextureFormat.PVRTCRGB4: return TextureFormat.PVRTC_RGB4;
                case SafeTextureFormat.PVRTCRGBA4: return TextureFormat.PVRTC_RGBA4;
                case SafeTextureFormat.DXT1Crunched: return TextureFormat.DXT1Crunched;
                case SafeTextureFormat.DXT5Crunched: return TextureFormat.DXT5Crunched;
                case SafeTextureFormat.ETC1Crunched: return TextureFormat.ETC_RGB4Crunched;
                case SafeTextureFormat.ETC2RGBA8Crunched: return TextureFormat.ETC2_RGBA8Crunched;
                default: return null;
            }
        }

        private static bool IsEtc(SafeTextureFormat value) => value == SafeTextureFormat.ETC2RGB ||
            value == SafeTextureFormat.ETC2RGBA8 || value == SafeTextureFormat.ETC1Crunched ||
            value == SafeTextureFormat.ETC2RGBA8Crunched;
        private static bool IsDesktop(SafeTextureFormat value) => value == SafeTextureFormat.BC1 || value == SafeTextureFormat.BC3 ||
            value == SafeTextureFormat.BC5 || value == SafeTextureFormat.BC7 || value == SafeTextureFormat.DXT1Crunched ||
            value == SafeTextureFormat.DXT5Crunched;
        private static bool IsMobile(SafeTextureFormat value) => value == SafeTextureFormat.ASTC4x4 || value == SafeTextureFormat.ASTC6x6 ||
            value == SafeTextureFormat.ASTC8x8 || value == SafeTextureFormat.ETC2RGB || value == SafeTextureFormat.ETC2RGBA8 ||
            value == SafeTextureFormat.PVRTCRGB4 || value == SafeTextureFormat.PVRTCRGBA4 || value == SafeTextureFormat.ETC1Crunched ||
            value == SafeTextureFormat.ETC2RGBA8Crunched;
        private static TextureSemantic Strictest(IEnumerable<TextureSemantic> values)
        {
            var list = values.Distinct().ToList();
            if (list.Contains(TextureSemantic.Normal)) return TextureSemantic.Normal;
            if (list.Contains(TextureSemantic.ColorAlpha)) return TextureSemantic.ColorAlpha;
            if (list.Contains(TextureSemantic.ColorOpaque)) return TextureSemantic.ColorOpaque;
            return TextureSemantic.Grayscale;
        }
    }
}
