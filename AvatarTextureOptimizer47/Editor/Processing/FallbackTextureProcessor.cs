using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.API;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using Fosa.AvatarTextureOptimizer.Editor.Reporting;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor.Processing
{
    /// <summary>EN: Whole-texture resizing for no-atlas mode and safe atlas fallbacks. ZH: 为无图集模式与安全回退执行整图缩放。</summary>
    internal static class FallbackTextureProcessor
    {
        public static void Process(BuildContext context, BuildPlan plan, BuildProgress progress,
            ResourceScope resources, AtoBuildReport report)
        {
            var atlasSources = plan.GeneratedLayers.SelectMany(x => x.Sources).ToHashSet();
            var usages = plan.Materials.Values.SelectMany(x => x.Usages).Where(x => !x.Protected && !atlasSources.Contains(x.Texture))
                .GroupBy(x => x.Texture).ToList();
            var shader = Shader.Find("Hidden/ATO/AtlasBlit");
            var finalizeShader = Shader.Find("Hidden/ATO/Finalize");
            if (shader == null || finalizeShader == null) throw new InvalidOperationException("ATO texture shaders were not found.");
            var material = resources.Own(new Material(shader) { hideFlags = HideFlags.HideAndDontSave });
            var finalizeMaterial = resources.Own(new Material(finalizeShader) { hideFlags = HideFlags.HideAndDontSave });

            for (var index = 0; index < usages.Count; index++)
            {
                progress.Report("Processing fallback textures / 处理回退贴图", index, Math.Max(1, usages.Count));
                var source = usages[index].Key;
                var semantic = StrictestSemantic(usages[index].Select(x => x.Semantic));
                var ratio = RequiredRatio(plan, source);
                var width = Mathf.Clamp(Mathf.CeilToInt(source.width * ratio.x), 1, source.width);
                var height = Mathf.Clamp(Mathf.CeilToInt(source.height * ratio.y), 1, source.height);
                var settings = plan.Profile.ForSemantic(semantic);
                var output = Resize(source, width, height, semantic, settings.mipmapsAndStreaming, material, finalizeMaterial);
                output.name = "ATO_" + source.name + "_Fallback";
                AtoExtensionRegistry.Postprocess(output, semantic);
                resources.Commit(output);
                context.AssetSaver.SaveAsset(output);
                plan.TextureReplacements[source] = output;
                ObjectRegistry.RegisterReplacedObject(source, output);
                foreach (var record in plan.Materials.Values)
                foreach (var usage in record.Usages.Where(x => x.Texture == source && !x.Protected))
                    if (record.Working.GetTexture(usage.PropertyName) == source) record.Working.SetTexture(usage.PropertyName, output);
                report.ProcessedTextureCount++;
                report.FallbackTextureCount++;
                report.Log($"Fallback texture {source.name}: {source.width}x{source.height} -> {width}x{height}.", plan.Component.settings.verboseLogging);
            }

            context.Extension<AnimatorServicesContext>().AnimationIndex.RewriteObjectCurves(obj =>
                obj is Texture2D texture && plan.TextureReplacements.TryGetValue(texture, out var replacement) ? replacement : obj);
        }

        private static Vector2 RequiredRatio(BuildPlan plan, Texture2D texture)
        {
            var ratio = Vector2.zero;
            var found = false;
            foreach (var group in plan.UvGroups.Where(x => x.Usages.Any(u => u.Texture == texture && !u.Protected)))
            foreach (var island in group.Islands)
            {
                found = true;
                ratio.x = Mathf.Max(ratio.x, (float)island.TargetPixelSize.x / Mathf.Max(1, island.SourcePixelSize.x));
                ratio.y = Mathf.Max(ratio.y, (float)island.TargetPixelSize.y / Mathf.Max(1, island.SourcePixelSize.y));
            }
            return found ? Vector2.Min(Vector2.one, ratio) : Vector2.one;
        }

        private static Texture2D Resize(Texture2D source, int width, int height, TextureSemantic semantic,
            bool mipmaps, Material material, Material finalizeMaterial)
        {
            var rt = new RenderTexture(new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBFloat, 0)
            { sRGB = false, useMipMap = false, autoGenerateMips = false })
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            rt.Create();
            try
            {
                material.SetTexture("_MainTex", source); material.SetInt("_Semantic", (int)semantic);
                material.SetInt("_AreaSample", width < source.width || height < source.height ? 1 : 0);
                Graphics.Blit(source, rt, material, 0);
                var encodeSrgb = semantic != TextureSemantic.Normal && semantic != TextureSemantic.Grayscale && source.isDataSRGB;
                var finalized = new RenderTexture(new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0)
                    { sRGB = false, useMipMap = false, autoGenerateMips = false })
                    { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                finalized.Create();
                try
                {
                    finalizeMaterial.SetInt("_EncodeSrgb", encodeSrgb ? 1 : 0);
                    Graphics.Blit(rt, finalized, finalizeMaterial, 0);
                    var output = new Texture2D(width, height, TextureFormat.RGBA32, mipmaps, !encodeSrgb)
                        { filterMode = source.filterMode, wrapModeU = source.wrapModeU, wrapModeV = source.wrapModeV,
                            anisoLevel = source.anisoLevel, mipMapBias = source.mipMapBias };
                    var previous = RenderTexture.active;
                    try { RenderTexture.active = finalized; output.ReadPixels(new Rect(0, 0, width, height), 0, 0, false); output.Apply(mipmaps, false); }
                    finally { RenderTexture.active = previous; }
                    return output;
                }
                finally { finalized.Release(); Object.DestroyImmediate(finalized); }
            }
            finally { rt.Release(); Object.DestroyImmediate(rt); }
        }

        private static TextureSemantic StrictestSemantic(IEnumerable<TextureSemantic> semantics)
        {
            var values = semantics.Distinct().ToList();
            if (values.Contains(TextureSemantic.Normal)) return TextureSemantic.Normal;
            if (values.Contains(TextureSemantic.ColorAlpha)) return TextureSemantic.ColorAlpha;
            if (values.Contains(TextureSemantic.ColorOpaque)) return TextureSemantic.ColorOpaque;
            return TextureSemantic.Grayscale;
        }
    }
}
