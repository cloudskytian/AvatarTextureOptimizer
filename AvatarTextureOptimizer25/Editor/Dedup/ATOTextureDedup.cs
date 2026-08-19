// Avatar Texture Optimizer / 头像贴图优化器
// Texture deduplication: textures are merged only when BOTH decoded pixel
// content AND import settings are identical. All references redirect to the
// representative; if ANY merged source was whitelisted, the merged result is
// whitelisted too (taint propagation).
// 贴图去重：仅当解码像素内容与导入设置都一致时才合并；所有引用重定向到代表；
// 若合并的任意来源含白名单，则合并结果同样视为白名单（污点传播）。

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Deduplicates textures in the usage model. / 对使用模型中的贴图去重。</summary>
    public static class ATOTextureDedup
    {
        /// <summary>
        /// Fill import metadata, hash content, then merge duplicates.
        /// 填充导入元数据、计算内容哈希，然后合并重复。
        /// </summary>
        public static void Run(ATOUsageModel model, AvatarTextureOptimizer settings, ATOProgress progress)
        {
            using (new ATOLog.Step("texture-dedup"))
            {
                if (!settings.deduplicateTextures)
                {
                    foreach (var kv in model.textures) ATOTextureIO.FillImportMetadata(kv.Value);
                    return;
                }

                // 1) metadata / 元数据
                var entries = model.textures.Values.ToList();
                for (int i = 0; i < entries.Count; i++)
                {
                    progress.Report("texture-meta", (float)i / entries.Count, entries[i].texture?.name);
                    ATOTextureIO.FillImportMetadata(entries[i]);
                }

                // 2) content hashes (sequential, memory friendly) / 内容哈希（顺序执行，内存友好）
                using (var pool = new ATORtPool(512L * 1024 * 1024))
                {
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var e = entries[i];
                        progress.ThrowIfCancelled();
                        progress.Report("texture-hash", (float)i / entries.Count, e.texture?.name);
                        try
                        {
                            e.contentHash = HashEntry(e, pool);
                        }
                        catch (System.Exception ex)
                        {
                            ATOLog.Warn($"hash failed for {e.texture?.name}: {ex.Message}");
                            e.exclusion |= ATOExcludeReason.NotTexture2D;
                            e.exclusionNote = "hash/readback failed / 回读失败";
                        }
                    }
                }

                // 3) group by signature+hash / 按 签名+哈希 分组
                var groups = new Dictionary<string, List<ATOTextureEntry>>();
                foreach (var e in entries)
                {
                    if (e.contentHash == null) continue;
                    var key = e.importSignature + "#" + e.contentHash;
                    if (!groups.TryGetValue(key, out var list))
                    {
                        list = new List<ATOTextureEntry>();
                        groups[key] = list;
                    }
                    list.Add(e);
                }

                // 4) merge: representative = lexicographically smallest asset path / 合并：代表=资产路径字典序最小
                foreach (var kv in groups)
                {
                    var list = kv.Value;
                    if (list.Count <= 1) continue;
                    list.Sort((a, b) => string.CompareOrdinal(a.assetPath ?? "", b.assetPath ?? ""));
                    var rep = list[0];
                    bool anyWhitelisted = list.Any(e => model.whitelistedTextures.Contains(e.texture) ||
                                                        (e.exclusion & ATOExcludeReason.UserWhitelist) != 0);
                    for (int i = 1; i < list.Count; i++)
                    {
                        var dup = list[i];
                        model.textureDedupMap[dup.texture] = rep.texture;
                        rep.sourceBytes = System.Math.Max(rep.sourceBytes, dup.sourceBytes);
                        // Propagate ALL exclusion flags to everyone / 排除标记双向传播
                        rep.exclusion |= dup.exclusion;
                        if (!string.IsNullOrEmpty(dup.exclusionNote) && string.IsNullOrEmpty(rep.exclusionNote))
                            rep.exclusionNote = dup.exclusionNote;
                        ATOLog.Verbose($"dedup: {dup.texture.name} -> {rep.texture.name}");
                    }
                    if (anyWhitelisted)
                    {
                        rep.exclusion |= ATOExcludeReason.DedupTainted | ATOExcludeReason.UserWhitelist;
                        model.whitelistedTextures.Add(rep.texture);
                        model.report.whitelistNotes.Add(ATOLoc.T("ato:dedup.tainted", rep.texture.name));
                    }
                    model.report.texturesDeduplicatedInto++;
                }

                // 5) re-point usages to representatives / 将用途重定向到代表
                foreach (var u in model.usages)
                {
                    if (u.texture == null) continue;
                    if (model.textureDedupMap.TryGetValue(u.texture.texture, out var repTex))
                    {
                        u.texture = model.EntryFor(repTex);
                        // Dedup taint / 去重污点
                        // (entry.exclusion already merged above / 表项排除已在上方合并)
                    }
                }

                // 6) wait: dump remaining unused entries / 标记完全不被引用的表项
                model.report.whitelistNotes.Add(ATOLoc.T("ato:dedup.summary",
                    model.report.texturesDeduplicatedInto, model.textures.Count));
            }
        }

        private static string HashEntry(ATOTextureEntry e, ATORtPool pool)
        {
            var tex = e.texture;
            if (tex == null) return null;
            Texture2D decoded = null;
            try
            {
                decoded = ATOTextureIO.Readback(tex, pool);
                return ATOTextureIO.ContentHash(decoded);
            }
            finally
            {
                if (decoded != null) Object.DestroyImmediate(decoded);
            }
        }
    }
}
