// Avatar Texture Optimizer (ATO)
// Texture dedup by content + import settings. Also dedups final atlases (stage 7).
// 按内容 + 导入设置对贴图去重。同时负责最终图集去重（阶段 7）。

using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Stages 3 & 7b: merge identical textures and update every reference.
    /// 阶段 3 与 7b：合并完全相同的贴图并更新全部引用。
    /// </summary>
    public static class ATOTextureDeduplicator
    {
        public static void Deduplicate(ATOBuildContext build, ATOProgress progress)
        {
            var groups = new Dictionary<string, List<ATOTextureRef>>();
            foreach (var tr in build.textures)
            {
                if (tr.texture == null) continue;
                var key = tr.importFingerprint;
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<ATOTextureRef>();
                list.Add(tr);
            }

            progress.Begin(groups.Count);
            int removed = 0;
            foreach (var kvp in groups)
            {
                var list = kvp.Value;
                if (list.Count <= 1) { progress.Advance(1); continue; }

                var canonical = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    var dup = list[i];
                    // Whitelist propagates into the dedup result. / 白名单传播到去重结果。
                    if (dup.isWhitelisted) canonical.isWhitelisted = true;
                    if (dup.skipAllOptimization) canonical.skipAllOptimization = true;

                    // Merge usages into canonical. / 将使用并入规范实例。
                    foreach (var u in dup.usages)
                    {
                        u.material?.SetTexture(u.propertyName, canonical.texture);
                        canonical.usages.Add(u);
                    }

                    // Record for animation rewriting. / 记录供动画改写。
                    build.animRemap.textureRemap[dup.texture] = canonical.texture;
                    removed++;
                }
                for (int i = 1; i < list.Count; i++) build.textures.Remove(list[i]);
                progress.Advance(1, $"merged {list.Count - 1} dups of {canonical.texture.name}");
            }

            build.textures.RemoveAll(t => t == null || t.texture == null);
            build.report.textureCountAfterDedup = build.textures.Count;
            ATOLogger.Info($"Texture dedup: removed {removed} duplicate textures; {build.textures.Count} remain.");
        }

        /// <summary>
        /// Stage 7: dedup generated atlases and final textures that are pixel-identical.
        /// 阶段 7：对像素完全一致的最终图集/贴图去重。
        /// </summary>
        public static void DeduplicateFinal(ATOBuildContext build, ATOProgress progress)
        {
            var seen = new Dictionary<string, Texture2D>();
            progress.Begin(build.atlases.Count + build.textures.Count);
            foreach (var at in build.atlases)
            {
                if (at.texture == null) { progress.Advance(1); continue; }
                var key = at.width + "x" + at.height + "|" + at.texture.imageContentsHash;
                if (seen.TryGetValue(key, out var existing) && existing != at.texture)
                {
                    foreach (var isl in at.islands)
                        build.animRemap.textureRemap[at.texture] = existing;
                    ATOLogger.Info($"Atlas '{at.name}' deduplicated into '{existing.name}'.");
                }
                else
                {
                    seen[key] = at.texture;
                }
                progress.Advance(1);
            }
        }
    }
}
