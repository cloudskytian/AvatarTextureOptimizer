using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: Deduplicates source textures by decoded pixel content combined with the import signature.
    ///     Two textures with identical pixels but different import settings are deliberately kept
    ///     separate, as required. Whitelisting is contagious: if any member of a duplicate class is
    ///     whitelisted, the survivor is whitelisted too.
    /// ZH: 按"解码像素内容 + 导入签名"对源贴图去重。
    ///     按需求，像素相同但导入设置不同的两张贴图刻意不合并。
    ///     白名单具有传染性：若重复类中任一成员在白名单内，则留存者也进入白名单。
    /// </summary>
    public static class TextureDeduplicator
    {
        /// <summary>EN: Run deduplication in place, filling <see cref="AtoTexture.DedupTarget"/>. ZH: 原地去重，填充 DedupTarget。</summary>
        /// <returns>EN: number of textures eliminated. ZH: 被消除的贴图数量。</returns>
        public static int Deduplicate(IReadOnlyCollection<AtoTexture> textures, ATOLog log)
        {
            var byHash = new Dictionary<Hash128, List<AtoTexture>>();
            foreach (var t in textures)
            {
                if (!byHash.TryGetValue(t.ContentHash, out var list))
                    byHash[t.ContentHash] = list = new List<AtoTexture>();
                list.Add(t);
            }

            int removed = 0;
            foreach (var kv in byHash)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;

                // EN: Keep the first as representative; propagate whitelist status onto it.
                // ZH: 保留第一张作为代表；把白名单状态传播到它身上。
                var keep = list[0];
                foreach (var t in list) if (t.Whitelisted) keep.Whitelisted = true;

                for (int i = 1; i < list.Count; i++)
                {
                    list[i].DedupTarget = keep;
                    removed++;
                }
                log.Detail($"Input dedup: {list.Count} identical textures collapsed into '{keep.Source.name}'");
            }

            log.Verbose($"Input texture dedup removed {removed} of {textures.Count} textures");
            return removed;
        }
    }
}
