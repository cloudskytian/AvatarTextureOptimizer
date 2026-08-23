using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using Fosa.AvatarTextureOptimizer.Editor.Reporting;
using nadena.dev.ndmf;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Processing
{
    /// <summary>EN: Content/parameter deduplication of generated atlases and fallback textures. ZH: 对生成图集与回退贴图执行内容/参数去重。</summary>
    internal static class GeneratedAssetDeduplicator
    {
        public static void DeduplicateTextures(BuildContext context, BuildPlan plan, BuildProgress progress,
            ResourceScope resources, AtoBuildReport report)
        {
            if (!plan.Component.settings.deduplicateTextures) return;
            var semanticByOutput = new Dictionary<Texture2D, TextureSemantic>();
            foreach (var layer in plan.GeneratedLayers) semanticByOutput[layer.Output] = layer.Semantic;
            foreach (var pair in plan.TextureReplacements)
            {
                if (semanticByOutput.ContainsKey(pair.Value)) continue;
                semanticByOutput[pair.Value] = Strictest(plan.Materials.Values.SelectMany(x => x.Usages)
                    .Where(x => x.Texture == pair.Key).Select(x => x.Semantic));
            }

            var replacements = new Dictionary<Texture2D, Texture2D>();
            foreach (var semanticGroup in semanticByOutput.GroupBy(x => x.Value))
            {
                var groupReplacements = TextureDeduplicator.Deduplicate(context, semanticGroup.Select(x => x.Key),
                    new HashSet<Texture2D>(), progress, resources, report);
                foreach (var pair in groupReplacements) replacements[pair.Key] = pair.Value;
            }
            if (replacements.Count == 0) return;
            foreach (var record in plan.Materials.Values)
            foreach (var property in record.Working.GetTexturePropertyNames())
                if (record.Working.GetTexture(property) is Texture2D texture && replacements.TryGetValue(texture, out var replacement))
                    record.Working.SetTexture(property, replacement);
            foreach (var layer in plan.GeneratedLayers)
                if (replacements.TryGetValue(layer.Output, out var replacement)) layer.Output = replacement;
            foreach (var key in plan.TextureReplacements.Keys.ToList())
                if (replacements.TryGetValue(plan.TextureReplacements[key], out var replacement)) plan.TextureReplacements[key] = replacement;
        }

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
