// English: Deduplicate Texture2D by importer fingerprint + pixel content, then rewrite references.
// 中文：按导入设置指纹 + 像素内容去重贴图，并更新引用。去重结果若含白名单则整体视为白名单。
using System.Collections.Generic;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOTextureDedup
    {
        public static void Run(ATOState state)
        {
            var groups = new Dictionary<string, List<Texture2D>>();
            var seen = new HashSet<Texture2D>();
            foreach (var use in state.Uses)
            {
                if (use.Texture == null || !seen.Add(use.Texture)) continue;
                var key = Fingerprint(state, use.Texture);
                List<Texture2D> list;
                if (!groups.TryGetValue(key, out list))
                {
                    list = new List<Texture2D>();
                    groups[key] = list;
                }

                list.Add(use.Texture);
            }

            var map = new Dictionary<Texture2D, Texture2D>();
            var merges = 0;
            foreach (var kv in groups)
            {
                if (kv.Value.Count < 2) continue;
                var keep = kv.Value[0];
                var anyWhite = false;
                for (var i = 0; i < kv.Value.Count; i++)
                {
                    if (state.WhitelistTextures.Contains(kv.Value[i])) anyWhite = true;
                }

                if (anyWhite)
                {
                    foreach (var t in kv.Value) state.WhitelistTextures.Add(t);
                    state.Log.VerboseInfo("dedup group contains whitelist; entire group marked whitelist key=" + Short(kv.Key));
                }

                for (var i = 1; i < kv.Value.Count; i++)
                {
                    map[kv.Value[i]] = keep;
                    merges++;
                }
            }

            if (merges == 0)
            {
                state.Log.Info("texture content dedup: no merges");
                return;
            }

            foreach (var use in state.Uses)
            {
                Texture2D repl;
                if (use.Texture != null && map.TryGetValue(use.Texture, out repl))
                    use.Texture = repl;
            }

            foreach (var kv in map) state.TextureReplace[kv.Key] = kv.Value;
            state.Report.TexturesDeduped += merges;
            state.Log.Info("texture content dedup merges=" + merges);
        }

        private static string Fingerprint(ATOState state, Texture2D tex)
        {
            var imp = ATOTextureCache.ImporterFingerprint(tex);
            var decoded = state.Cache.Get(tex, state.Log);
            if (decoded == null || decoded.Pixels == null) return imp + "|nodecode";
            using (var md5 = MD5.Create())
            {
                var bytes = new byte[decoded.Pixels.Length * 4];
                var p = decoded.Pixels;
                for (var i = 0; i < p.Length; i++)
                {
                    var o = i * 4;
                    bytes[o] = p[i].r;
                    bytes[o + 1] = p[i].g;
                    bytes[o + 2] = p[i].b;
                    bytes[o + 3] = p[i].a;
                }

                var hash = md5.ComputeHash(bytes);
                return imp + "|" + BitConverter.ToString(hash);
            }
        }

        private static string Short(string s)
        {
            if (s == null) return "";
            return s.Length <= 64 ? s : s.Substring(0, 64) + "...";
        }
    }
}
