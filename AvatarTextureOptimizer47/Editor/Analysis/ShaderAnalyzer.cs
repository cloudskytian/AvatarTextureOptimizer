using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.API;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>EN: Shader-property introspection with a reject-by-default safety policy. ZH: 默认拒绝策略的 Shader 属性内省。</summary>
    internal static class ShaderAnalyzer
    {
        private static readonly string[] SafeColorTokens = { "maintex", "main2ndtex", "main3rdtex", "basemap", "basecolor", "albedo", "emission", "outline" };
        private static readonly string[] NormalTokens = { "normal", "bump" };
        private static readonly string[] GrayTokens = { "mask", "metallic", "roughness", "smoothness", "occlusion", "specular", "thickness", "parallax" };
        private static readonly string[] UnsafeTokens = { "decal", "matcap", "lut", "gradation", "audio", "screen", "reflection", "cube", "environment", "noise", "dither", "parallax" };
        private static readonly string[] TransformSuffixes = { "_ST", "_ScrollRotate", "_Rotation", "_Rotate", "_UVTransform" };
        private static readonly Dictionary<Texture2D, bool> AlphaCache = new Dictionary<Texture2D, bool>();

        public static void BeginAnalysis() => AlphaCache.Clear();

        public static List<TextureUsage> Analyze(Material material, Renderer renderer, int slot, string rendererPath,
            AnimationSnapshot animation, out string materialUnsafeReason)
        {
            materialUnsafeReason = null;
            var output = new List<TextureUsage>();
            if (material == null || material.shader == null) return output;
            var shader = material.shader;
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                var property = shader.GetPropertyName(i);
                var assigned = material.GetTexture(property);
                if (assigned == null) continue;
                if (shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D || !(assigned is Texture2D))
                {
                    materialUnsafeReason = $"Material also uses unsupported texture type at {property}";
                    continue;
                }
                var texture = (Texture2D)assigned;

                var hasCustom = TryCustom(material, property, texture, out var custom);
                var usage = new TextureUsage
                {
                    Material = material,
                    PropertyName = property,
                    Texture = texture,
                    Semantic = hasCustom ? custom.semantic : DetermineSemantic(property, texture),
                    UvChannel = hasCustom ? custom.uvChannel : DetermineUvChannel(material, property),
                    FilterMode = texture.filterMode,
                    IsSrgb = texture.isDataSRGB,
                    IsAnimated = animation.IsAnimated(rendererPath, property),
                    UsedChannelMask = hasCustom ? custom.usedChannelMask : DetermineUsedChannels(property),
                };
                usage.Renderers.Add(renderer);
                usage.AlphaConstraints.AddRange(DetermineAlphaConstraints(material, rendererPath, animation));
                usage.UnsafeReason = hasCustom
                    ? (custom.safe ? null : custom.unsafeReason ?? "Rejected by custom analyzer")
                    : ValidateUsage(material, property, rendererPath, animation, shader.GetPropertyAttributes(i), usage.UvChannel);
                if (usage.Safe && GraphicsFormatUtility.IsHDRFormat(texture.graphicsFormat))
                    usage.UnsafeReason = "HDR texture output is not safely supported";
                output.Add(usage);
            }
            if (!string.IsNullOrEmpty(materialUnsafeReason))
                foreach (var usage in output) usage.UnsafeReason = materialUnsafeReason;
            return output;
        }

        private static bool TryCustom(Material material, string property, Texture2D texture,
            out AtoTextureUsageDescriptor descriptor)
        {
            foreach (var analyzer in AtoExtensionRegistry.Get<IAtoTexturePropertyAnalyzer>())
            {
                try { if (analyzer.TryAnalyze(material, property, texture, out descriptor)) return true; }
                catch (Exception ex) { Debug.LogWarning($"[ATO] Custom shader analyzer {analyzer.GetType().FullName} failed: {ex.Message}", material); }
            }
            descriptor = default;
            return false;
        }

        private static string ValidateUsage(Material material, string property, string path, AnimationSnapshot animation,
            string[] attributes, int uvChannel)
        {
            var lower = property.ToLowerInvariant();
            if (uvChannel < 0 || uvChannel > 7) return "Non-mesh or unknown UV source";
            if (UnsafeTokens.Any(lower.Contains)) return "Texture property has a special/non-mesh semantic";
            if (!IsKnownSafeName(lower)) return "Shader texture property semantics are unknown";

            var noScaleOffset = attributes != null && attributes.Any(x => x.Equals("NoScaleOffset", StringComparison.OrdinalIgnoreCase));
            if (!noScaleOffset)
            {
                if (material.GetTextureScale(property) != Vector2.one || material.GetTextureOffset(property) != Vector2.zero)
                    return "Texture ST is not identity";
                if (animation.IsAnimated(path, property + "_ST")) return "Texture ST is animated";
            }

            foreach (var suffix in TransformSuffixes.Skip(1))
            {
                var transformProperty = property + suffix;
                if (material.HasProperty(transformProperty) && material.GetVector(transformProperty) != Vector4.zero)
                    return $"Texture transform {transformProperty} is not identity";
                if (animation.IsAnimated(path, transformProperty)) return $"Texture transform {transformProperty} is animated";
            }
            return null;
        }

        internal static string CanonicalRole(string property)
        {
            var lower = (property ?? string.Empty).ToLowerInvariant();
            if (lower == "_maintex" || lower == "_basemap" || lower == "_basecolormap" || lower.Contains("albedo")) return "MainColor";
            if (lower == "_bumpmap" || lower == "_normalmap") return "PrimaryNormal";
            if (lower.Contains("emission")) return "Emission";
            return property ?? string.Empty;
        }

        private static bool IsKnownSafeName(string lower)
        {
            return SafeColorTokens.Any(lower.Contains) || NormalTokens.Any(lower.Contains) || GrayTokens.Any(lower.Contains) ||
                   lower == "_detailalbedomap" || lower == "_detailnormalmap" || lower == "_alphamask";
        }

        private static TextureSemantic DetermineSemantic(string property, Texture2D texture)
        {
            var lower = property.ToLowerInvariant();
            var path = AssetDatabase.GetAssetPath(texture);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null && importer.textureType == TextureImporterType.NormalMap) return TextureSemantic.Normal;
            if (NormalTokens.Any(lower.Contains)) return TextureSemantic.Normal;
            if (GrayTokens.Any(lower.Contains) || lower.Contains("alphamask")) return TextureSemantic.Grayscale;
            return HasAlpha(texture) ? TextureSemantic.ColorAlpha : TextureSemantic.ColorOpaque;
        }

        private static int DetermineUvChannel(Material material, string property)
        {
            foreach (var candidate in new[] { property + "_UVMode", property + "UVMode" })
            {
                if (!material.HasProperty(candidate)) continue;
                var value = Mathf.RoundToInt(material.GetFloat(candidate));
                return value >= 0 && value <= 3 ? value : -1;
            }
            if (property.Equals("_DetailAlbedoMap", StringComparison.OrdinalIgnoreCase) ||
                property.Equals("_DetailNormalMap", StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        private static int DetermineUsedChannels(string property)
        {
            var lower = property.ToLowerInvariant();
            if (lower.Contains("metallic") || lower.Contains("smoothness")) return 0x9;
            if (lower.Contains("occlusion")) return 0x2;
            if (lower.Contains("roughness")) return 0x1;
            if (lower.Contains("mask")) return 0xF;
            return 0xF;
        }

        private static IEnumerable<AlphaConstraint> DetermineAlphaConstraints(Material material, string path,
            AnimationSnapshot animation)
        {
            var cutoff = material.HasProperty("_Cutoff") ? material.GetFloat("_Cutoff") : 0.5f;
            var values = animation.ValuesFor(path, "_Cutoff").Concat(animation.ValuesFor(path, "_AlphaCutoff")).ToList();
            if (values.Count == 0) values.Add(cutoff);
            var possibleModes = new HashSet<AlphaMode> { DetermineStaticAlphaMode(material) };
            foreach (var property in new[] { "_Surface", "_Mode", "_RenderingMode", "_TransparentMode", "_AlphaClip" })
                if (animation.IsAnimated(path, property)) { possibleModes.Add(AlphaMode.Cutout); possibleModes.Add(AlphaMode.Blend); }
            foreach (var mode in possibleModes)
            foreach (var value in values) yield return new AlphaConstraint(mode, Mathf.Clamp01(value));
        }

        private static AlphaMode DetermineStaticAlphaMode(Material material)
        {
            if (material.IsKeywordEnabled("_ALPHATEST_ON") || material.renderQueue >= (int)RenderQueue.AlphaTest && material.renderQueue < (int)RenderQueue.Transparent)
                return AlphaMode.Cutout;
            if (material.IsKeywordEnabled("_ALPHABLEND_ON") || material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") || material.renderQueue >= (int)RenderQueue.Transparent)
                return AlphaMode.Blend;
            return AlphaMode.Opaque;
        }

        private static bool HasAlpha(Texture2D texture)
        {
            if (!GraphicsFormatUtility.HasAlphaChannel(texture.graphicsFormat)) return false;
            if (AlphaCache.TryGetValue(texture, out var cached)) return cached;
            const int stripeHeight = 128;
            var hasAlpha = false;
            for (var y = 0; y < texture.height && !hasAlpha; y += stripeHeight)
            {
                var height = Mathf.Min(stripeHeight, texture.height - y);
                var target = RenderTexture.GetTemporary(texture.width, height, 0, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Linear);
                var previous = RenderTexture.active;
                Texture2D readback = null;
                try
                {
                    Graphics.Blit(texture, target, new Vector2(1f, (float)height / texture.height),
                        new Vector2(0f, (float)y / texture.height));
                    RenderTexture.active = target;
                    readback = new Texture2D(texture.width, height, TextureFormat.RGBA32, false, true);
                    readback.ReadPixels(new Rect(0, 0, texture.width, height), 0, 0, false);
                    readback.Apply(false, false);
                    var pixels = readback.GetRawTextureData<Color32>();
                    for (var i = 0; i < pixels.Length; i++)
                        if (pixels[i].a < 255) { hasAlpha = true; break; }
                }
                finally
                {
                    RenderTexture.active = previous;
                    if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
                    RenderTexture.ReleaseTemporary(target);
                }
            }
            AlphaCache[texture] = hasAlpha;
            return hasAlpha;
        }
    }
}
