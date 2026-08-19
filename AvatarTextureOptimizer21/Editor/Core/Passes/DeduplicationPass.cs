// Deduplication Pass - Deduplicates textures by content and import settings
// 去重Pass - 按内容和导入设置对贴图进行去重

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.Runtime;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Core.Passes
{
    /// <summary>
    /// Deduplicates textures based on pixel content and import settings.
    /// If a whitelisted texture is deduped, the dedup target is also whitelisted.
    /// 基于像素内容和导入设置对贴图进行去重。
    /// 如果白名单贴图被去重，去重目标也将视为白名单。
    /// </summary>
    public class DeduplicationPass : Pass<DeduplicationPass>
    {
        public override string DisplayName => "ATO: Texture Deduplication / 贴图去重";

        protected override void Execute(BuildContext context)
        {
            var sw = Stopwatch.StartNew();
            var atoCtx = context.GetState<ATOBuildContext>();
            if (!atoCtx.IsValid) return;

            ATOLog.Info("Starting texture deduplication...");
            ATOLog.Info("开始贴图去重...");

            // Group textures by content hash + import settings
            // 按内容哈希+导入设置对贴图进行分组
            var hashGroups = new Dictionary<string, List<TextureInfo>>();

            foreach (var texInfo in atoCtx.AllTextures)
            {
                if (texInfo.Texture == null) continue;

                string hash = TextureHelper.GetTextureContentHash(texInfo.Texture);
                // Include import settings in the hash
                hash += $"_{texInfo.Width}x{texInfo.Height}_{texInfo.WrapMode}_{texInfo.FilterMode}";

                if (!hashGroups.ContainsKey(hash))
                    hashGroups[hash] = new List<TextureInfo>();
                hashGroups[hash].Add(texInfo);
            }

            int dedupCount = 0;
            foreach (var group in hashGroups.Values)
            {
                if (group.Count <= 1) continue;

                // Find the "canonical" texture (prefer non-whitelisted, largest)
                var canonical = group.OrderByDescending(t => t.Width * t.Height).First();

                // Check if any in the group is whitelisted
                bool anyWhitelisted = group.Any(t => t.IsWhitelisted);

                foreach (var texInfo in group)
                {
                    if (texInfo == canonical) continue;

                    // Replace all references to this texture with the canonical one
                    ReplaceTextureReferences(atoCtx, texInfo.Texture, canonical.Texture);
                    texInfo.Texture = canonical.Texture;

                    // If any was whitelisted, mark all as whitelisted
                    if (anyWhitelisted)
                    {
                        texInfo.IsWhitelisted = true;
                        atoCtx.WhitelistedTextureIds.Add(canonical.Texture.GetInstanceID());
                    }

                    dedupCount++;
                }
            }

            ATOLog.Info($"Deduplication complete: {dedupCount} textures deduplicated.");
            ATOLog.Info($"去重完成：{dedupCount}张贴图已去重。");

            atoCtx.ReportEntries.Add(new ReportEntry
            {
                Severity = ReportSeverity.Info,
                Category = "Deduplication / 去重",
                Message = $"Deduplicated {dedupCount} textures",
                MessageZh = $"去重了{dedupCount}张贴图"
            });

            sw.Stop();
            atoCtx.StageTimings["Deduplication"] = sw.Elapsed.TotalMilliseconds;
        }

        private void ReplaceTextureReferences(ATOBuildContext atoCtx, Texture2D oldTex, Texture2D newTex)
        {
            // Update UV texture mappings
            foreach (var kvp in atoCtx.UVTextureMap)
            {
                foreach (var usage in kvp.Value.TextureUsages)
                {
                    if (usage.Texture == oldTex)
                        usage.Texture = newTex;
                }
            }
        }
    }
}
