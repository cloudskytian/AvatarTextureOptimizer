using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Apply
{
    // 图集去重（构建后）：内容与参数完全相同的图集合并，更新岛引用并删除重复资产。
    // Post-build atlas dedup: byte-identical atlases merge; island references update; duplicate assets removed.
    internal static class TextureDedupPost
    {
        public static void Merge(ATOContext ctx, ATOReport.Stage stage)
        {
            if (!ctx.settings.deduplicateTextures) return;
            int merged = 0;

            var groups = new Dictionary<string, List<Packing.AtlasPlan>>();
            foreach (var plan in ctx.atlasPlans)
            {
                string key = plan.kind + "|" + plan.width + "x" + plan.height;
                List<Packing.AtlasPlan> list;
                if (!groups.TryGetValue(key, out list))
                {
                    list = new List<Packing.AtlasPlan>();
                    groups[key] = list;
                }
                list.Add(plan);
            }

            foreach (var kv in groups)
            {
                ctx.CheckCancelled();
                var list = kv.Value;
                if (list.Count < 2) continue;
                var kept = list[0];
                var keptBytes = ReadPng(kept);
                for (int i = 1; i < list.Count; i++)
                {
                    var dup = list[i];
                    var dupBytes = ReadPng(dup);
                    if (keptBytes == null || dupBytes == null || keptBytes.Length != dupBytes.Length) continue;
                    bool same = true;
                    for (int b = 0; b < keptBytes.Length; b++)
                    {
                        if (keptBytes[b] != dupBytes[b]) { same = false; break; }
                    }
                    if (!same) continue;

                    // 重定向岛引用。Redirect island references.
                    foreach (var e in dup.islands)
                    {
                        if (e.atlasId == dup.id)
                        {
                            e.atlasId = kept.id;
                        }
                        foreach (var u in e.uses)
                        {
                            if (u.replacementTexture == dup.texture)
                            {
                                u.replacementTexture = kept.texture;
                                u.replacementAtlas = kept;
                            }
                        }
                    }
                    foreach (var entry in ctx.textures)
                    {
                        if (entry.replacementTexture == dup.texture) entry.replacementTexture = kept.texture;
                    }
                    // 删除重复资产。Remove the duplicate asset.
                    if (!string.IsNullOrEmpty(dup.assetPath))
                    {
                        AssetDatabase.DeleteAsset(dup.assetPath);
                    }
                    dup.texture = kept.texture;
                    merged++;
                    stage.AddLine(string.Format(ATOLocalization.Tr("log.atlasDedup"), dup.ToString(), kept.ToString()));
                }
            }
            if (merged > 0) stage.AddLine(string.Format(ATOLocalization.Tr("log.atlasDedupSummary"), merged));
        }

        private static byte[] ReadPng(Packing.AtlasPlan plan)
        {
            if (string.IsNullOrEmpty(plan.assetPath) || !File.Exists(plan.assetPath)) return null;
            return File.ReadAllBytes(plan.assetPath);
        }
    }
}
