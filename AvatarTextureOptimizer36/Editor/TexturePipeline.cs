using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Fosa.AvatarTextureOptimizer;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Whole-texture and atlas-fallback processing. / 整图与图集失败回退处理。
    /// </summary>
    internal static class TexturePipeline
    {
        public static void OptimizeWholeTextures(BuildSnapshot snapshot, ATOBuildSession.BuildContextAdapter context,
            AvatarTextureOptimizer component, ATOPlatformOptions options, ATOLogger logger, ATOProgress progress,
            ATOBuildReport report)
        {
            if (!options.optimizeTextures || !component.optimizeTextures) return;
            OptimizeFallbackReferences(snapshot, context, component, options, logger, report);
            progress.Step(0.75f, "Resize whole textures / 缩放整图");
        }

        public static void OptimizeFallbackReferences(BuildSnapshot snapshot, ATOBuildSession.BuildContextAdapter context,
            AvatarTextureOptimizer component, ATOPlatformOptions options, ATOLogger logger, ATOBuildReport report)
        {
            if (!options.optimizeTextures || !component.optimizeTextures) return;
            Dictionary<TextureAssetInfo, Texture2D> optimizedBySource = new Dictionary<TextureAssetInfo, Texture2D>();
            for (int i = 0; i < snapshot.MaterialUses.Count; i++)
            {
                MaterialUse use = snapshot.MaterialUses[i];
                if (use.SkipAll || use.SourceMaterial == null) continue;
                bool materialChanged = false;
                for (int referenceIndex = 0; referenceIndex < use.References.Count; referenceIndex++)
                {
                    TextureReference reference = use.References[referenceIndex];
                    if (reference == null || reference.Texture == null) continue;
                    if (reference.AtlasAssigned)
                    {
                        if (reference.OptimizedTexture != null)
                        {
                            Material material = EnsureWorkingMaterial(use, context);
                            material.SetTexture(reference.PropertyName, reference.OptimizedTexture);
                            materialChanged = true;
                        }
                        continue;
                    }
                    if (reference.IsWhitelisted)
                    {
                        reference.OptimizedTexture = reference.Texture.Source;
                        continue;
                    }
                    Texture2D optimized;
                    if (!optimizedBySource.TryGetValue(reference.Texture, out optimized))
                    {
                        optimized = CreateOptimizedTexture(reference.Texture, reference.Category, snapshot, context,
                            component, options, logger);
                        optimizedBySource.Add(reference.Texture, optimized);
                    }
                    reference.OptimizedTexture = optimized;
                    if (optimized != null && optimized != reference.Texture.Source)
                    {
                        Material material = EnsureWorkingMaterial(use, context);
                        material.SetTexture(reference.PropertyName, optimized);
                        materialChanged = true;
                    }
                }
                if (materialChanged) ApplyWorkingMaterial(use.Owner);
            }
        }

        private static Texture2D CreateOptimizedTexture(TextureAssetInfo source, ATOTextureCategory category,
            BuildSnapshot snapshot, ATOBuildSession.BuildContextAdapter context, AvatarTextureOptimizer component,
            ATOPlatformOptions options, ATOLogger logger)
        {
            if (source == null || source.Source == null) return null;
            int targetWidth = source.Width;
            int targetHeight = source.Height;
            if (options.maxSourceTextureSize > 0 && Mathf.Max(targetWidth, targetHeight) > options.maxSourceTextureSize)
            {
                float scale = options.maxSourceTextureSize / (float)Mathf.Max(targetWidth, targetHeight);
                targetWidth = Mathf.Max(1, Mathf.FloorToInt(targetWidth * scale));
                targetHeight = Mathf.Max(1, Mathf.FloorToInt(targetHeight * scale));
            }

            bool needsCopy = targetWidth != source.Width || targetHeight != source.Height ||
                             source.Fingerprint.Mipmap != options.enableMipStreaming || source.Fingerprint.Streaming != options.enableMipStreaming;
            // A build-local copy is used when import settings must change; source assets are never mutated globally.
            // 当导入设置需要变化时创建构建本地副本，绝不全局修改源资产。
            if (!needsCopy && component.qualityParameters.targetQuality >= 0.999999f)
                needsCopy = true;
            if (!needsCopy) return source.Source;

            TexturePixelData data = snapshot.PixelCache.Get(source.Source, logger);
            if (data == null) return source.Source;
            string name = "ATO_" + Sanitize(source.DisplayName) + "_Fallback";
            bool exactCopy = targetWidth == source.Width && targetHeight == source.Height &&
                             component.qualityParameters.targetQuality >= 0.999999f;
            Texture2D generated = GeneratedTextureWriter.CreateAndSave(context, targetWidth, targetHeight, name,
                exactCopy
                    ? (raw, covered) => FillExact(raw, covered, data)
                    : (raw, covered) => FillResized(raw, covered, targetWidth, targetHeight, data, category),
                category, ATOPlatformResolver.Current(), options, source, logger);
            return generated == null ? source.Source : generated;
        }

        private static void FillExact(NativeArray<Color32> raw, BitArray covered, TexturePixelData source)
        {
            int count = Mathf.Min(raw.Length, source.Pixels.Length);
            for (int i = 0; i < count; i++)
            {
                raw[i] = source.Pixels[i];
                covered[i] = true;
            }
        }

        private static void FillResized(NativeArray<Color32> raw, BitArray covered, int width, int height,
            TexturePixelData source, ATOTextureCategory category)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    Color32 value = AtlasPixelSampler.Sample(source, (x + 0.5f) / width, (y + 0.5f) / height, category);
                    int index = y * width + x;
                    raw[index] = value;
                    covered[index] = true;
                }
        }

        private static Material EnsureWorkingMaterial(MaterialUse use, ATOBuildSession.BuildContextAdapter context)
        {
            if (use.WorkingMaterial != null) return use.WorkingMaterial;
            use.WorkingMaterial = UnityEngine.Object.Instantiate(use.SourceMaterial);
            use.WorkingMaterial.name = "ATO_" + use.SourceMaterial.name + "_Material";
            context.RegisterReplacement(use.SourceMaterial, use.WorkingMaterial);
            return use.WorkingMaterial;
        }

        private static void ApplyWorkingMaterial(RendererRecord renderer)
        {
            Material[] materials = renderer.Renderer.sharedMaterials;
            for (int i = 0; i < renderer.Materials.Count; i++)
            {
                MaterialUse use = renderer.Materials[i];
                if (use.WorkingMaterial != null && use.Slot >= 0 && use.Slot < materials.Length) materials[use.Slot] = use.WorkingMaterial;
            }
            renderer.Renderer.sharedMaterials = materials;
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Texture";
            return new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
        }
    }

    /// <summary>
    /// Applies the final mip/streaming/wrap/format policy to generated files. / 对生成文件应用最终 mip、streaming、wrap、格式策略。
    /// </summary>
    internal static class TextureImportPipeline
    {
        public static void Apply(BuildSnapshot snapshot, ATOBuildSession.BuildContextAdapter context,
            AvatarTextureOptimizer component, ATOPlatformOptions options, ATOLogger logger, ATOBuildReport report)
        {
            // GeneratedTextureWriter configures every generated PNG immediately; this pass verifies the invariant.
            // GeneratedTextureWriter 已即时配置所有生成 PNG，本阶段只验证不变量。
            for (int i = 0; i < snapshot.MaterialUses.Count; i++)
            {
                MaterialUse use = snapshot.MaterialUses[i];
                for (int j = 0; j < use.References.Count; j++)
                {
                    TextureReference reference = use.References[j];
                    if (reference == null || reference.OptimizedTexture == null || reference.IsWhitelisted) continue;
                    TextureImporter importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(reference.OptimizedTexture)) as TextureImporter;
                    if (importer == null) continue;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.mipmapEnabled = options.enableMipStreaming;
                    importer.streamingMipmaps = options.enableMipStreaming;
                    importer.isReadable = false;
                    EditorUtility.SetDirty(importer);
                }
            }
            AssetDatabase.SaveAssets();
        }
    }
}
