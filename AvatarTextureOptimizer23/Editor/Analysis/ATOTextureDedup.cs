using System.Collections.Generic;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Dedup by pixel hash + import settings. If any member is whitelist, the survivor is whitelist.
    /// 按像素哈希 + 导入设置去重。集合里只要有白名单，结果也是白名单。
    /// </summary>
    internal static class ATOTextureDedup
    {
        public static void Run(ATOContext ctx)
        {
            var groups = new Dictionary<string, List<Texture2D>>();
            var seen = new HashSet<Texture2D>();

            foreach (var use in ctx.Uses)
            {
                var tex = use.Slot.texture;
                if (tex == null || !seen.Add(tex)) continue;
                if (ctx.WhitelistedTextures.Contains(tex)) continue; // still hashed below if mixed
                var key = Fingerprint(ctx, tex);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<Texture2D>();
                    groups[key] = list;
                }
                list.Add(tex);
            }

            // Also group whitelist textures so a whitelist+duplicate pair stays whitelist.
            // 白名单贴图也参与分组，保证“白名单 + 副本”的结果仍是白名单。
            foreach (var tex in ctx.WhitelistedTextures)
            {
                if (tex == null || !seen.Add(tex)) continue;
                var key = Fingerprint(ctx, tex);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<Texture2D>();
                    groups[key] = list;
                }
                list.Add(tex);
            }

            var remap = ctx.TextureRemap;
            int collapsed = 0;
            foreach (var kv in groups)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;
                Texture2D survivor = list[0];
                var anyWhite = false;
                foreach (var t in list)
                {
                    if (ctx.WhitelistedTextures.Contains(t))
                    {
                        anyWhite = true;
                        survivor = t;
                        break;
                    }
                }
                foreach (var t in list)
                {
                    if (t == survivor) continue;
                    remap[t] = survivor;
                    collapsed++;
                    if (anyWhite) ctx.WhitelistedTextures.Add(t);
                }
                if (anyWhite) ctx.WhitelistedTextures.Add(survivor);
                ctx.Log.Detail($"Dedup group {kv.Key.Substring(0, Mathf.Min(12, kv.Key.Length))}… → {survivor.name} (n={list.Count}, white={anyWhite})");
            }

            if (remap.Count > 0)
            {
                foreach (var use in ctx.Uses)
                {
                    if (use.Slot.texture != null && remap.TryGetValue(use.Slot.texture, out var s))
                        use.Slot.texture = s;
                }
            }

            ctx.Log.Info($"Texture dedup collapsed {collapsed} references");
        }

        private static string Fingerprint(ATOContext ctx, Texture2D tex)
        {
            var dec = ATOTextureUtil.Decode(ctx, tex);
            var hash = ATOTextureUtil.PixelHash(dec.Pixels);
            var imp = ATOTextureUtil.ImportFingerprint(tex);
            return $"{hash:X16}|{tex.width}x{tex.height}|{imp}";
        }
    }
}
