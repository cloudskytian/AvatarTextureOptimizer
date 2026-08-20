using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Stage: deduplicate identical textures (actual pixel content + import settings; different
    /// import settings are treated as different textures). All references are updated via the
    /// remapper. If any duplicate is whitelisted, the dedupe result is whitelisted too. /
    /// 阶段：去重相同贴图（实际像素内容+导入设置；导入设置不同视为不同）。全部引用经重映射器更新。
    /// 若去重对象中任一在白名单，则去重结果也视为白名单。
    /// </summary>
    internal sealed class AtoStageDedupeTextures : IAtoStage
    {
        public string I18nKey => "dedupeTextures";

        public void Run(AtoContext ctx)
        {
            var state = ctx.State;
            var textures = ctx.Textures.Values.ToList();
            var groups = new Dictionary<string, List<AtoTextureRecord>>();

            for (var i = 0; i < textures.Count; i++)
            {
                state.SetProgress($"hashing {i + 1}/{textures.Count}", (float)i / Mathf.Max(1, textures.Count));
                var record = textures[i];
                var importSettings = AtoTextureIO.GetImportSettings(record.Texture);
                var key = AtoTextureIO.GetDedupeKey(record.Texture, importSettings);
                record.DedupeHash = key;
                if (!groups.TryGetValue(key, out var list))
                {
                    groups[key] = list = new List<AtoTextureRecord>();
                }
                list.Add(record);
            }

            var mergedCount = 0;
            foreach (var group in groups.Values)
            {
                if (group.Count <= 1) continue;

                var representative = group[0];
                for (var j = 1; j < group.Count; j++)
                {
                    var duplicate = group[j];
                    AtoLog.Verbose($"[ATO] dedupe: {duplicate.Texture.name} == {representative.Texture.name}");

                    // Whitelist status union: if any is whitelisted, the result is whitelisted. /
                    // 白名单并集：任一在白名单则结果白名单。
                    if (duplicate.Whitelisted && !representative.Whitelisted)
                    {
                        representative.Whitelisted = true;
                        representative.WhitelistReason = duplicate.WhitelistReason;
                        ctx.WhitelistedTextures[representative.Texture] = duplicate.WhitelistReason;
                    }
                    else if (ctx.WhitelistedTextures.TryGetValue(duplicate.Texture, out var duplicateReason) &&
                             !ctx.WhitelistedTextures.ContainsKey(representative.Texture))
                    {
                        ctx.WhitelistedTextures[representative.Texture] = duplicateReason;
                        representative.Whitelisted = true;
                        representative.WhitelistReason = duplicateReason;
                    }

                    // Merge slots & remap references. / 合并槽位并重映射引用。
                    foreach (var slot in duplicate.Slots)
                    {
                        slot.Texture = representative.Texture;
                        representative.Slots.Add(slot);
                    }
                    duplicate.Slots.Clear();

                    // All lookups for the duplicate texture now resolve to the representative. /
                    // 对重复贴图的一切查找都落到代表贴图。
                    ctx.Textures[duplicate.Texture] = representative;
                    ctx.Remapper.Register(duplicate.Texture, representative.Texture);

                    mergedCount++;
                }
            }

            // Drop empty records (all merged). / 移除已全部合并的空记录。
            var liveRecords = new Dictionary<Texture2D, AtoTextureRecord>();
            foreach (var kv in ctx.Textures)
            {
                if (kv.Value.Slots.Count > 0) liveRecords[kv.Key] = kv.Value;
            }
            ctx.Textures = liveRecords;

            AtoLog.Info($"[ATO] texture dedupe: {mergedCount} texture(s) merged away, {ctx.Textures.Count} remain.");
        }
    }
}
