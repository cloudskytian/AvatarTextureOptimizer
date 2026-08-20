using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class TextureDeduplicator
    {
        public static Dictionary<Texture2D, Texture2D> DedupAndRetarget(List<SlotBinding> bindings, HashSet<Texture> whitelist, BakeReport report)
        {
            var map = new Dictionary<TextureKey, Texture2D>();
            var remap = new Dictionary<Texture2D, Texture2D>();
            foreach (var b in bindings)
            {
                var tex = b.Tex.Texture;
                if (tex == null) continue;
                int hash = 0;
                try
                {
                    if (tex.isReadable)
                    {
                        var px = tex.GetPixels32();
                        hash = HashPixels(px);
                    }
                    else hash = tex.GetNativeTexturePtr().ToInt32() ^ tex.width ^ tex.height;
                }
                catch
                {
                    hash = tex.GetInstanceID();
                }

                bool srgb = !GraphicsFormatIsLinear(tex);
                var key = new TextureKey(tex, hash, srgb);
                if (!map.TryGetValue(key, out var canon))
                {
                    map[key] = tex;
                    canon = tex;
                }
                else if (canon != tex)
                {
                    remap[tex] = canon;
                    if (whitelist.Contains(tex) || whitelist.Contains(canon))
                    {
                        whitelist.Add(tex);
                        whitelist.Add(canon);
                        b.Whitelisted = true;
                    }
                    b.Tex = new ShaderPropertyAnalyzer.Binding
                    {
                        Property = b.Tex.Property,
                        Texture = canon,
                        Semantic = b.Tex.Semantic,
                        UvChannel = b.Tex.UvChannel,
                        HasST = b.Tex.HasST,
                        ST = b.Tex.ST,
                        Known = b.Tex.Known
                    };
                    if (b.Material != null && b.Material.HasProperty(b.Tex.Property))
                        b.Material.SetTexture(b.Tex.Property, canon);
                    AtoLog.VerboseInfo($"Dedup {tex.name} -> {canon.name}");
                }
            }
            AtoLog.Info($"Texture dedup remaps={remap.Count}");
            return remap;
        }

        static int HashPixels(Color32[] px)
        {
            unchecked
            {
                int h = 17;
                int step = Mathf.Max(1, px.Length / 4096);
                for (int i = 0; i < px.Length; i += step)
                    h = h * 31 + px[i].r + (px[i].g << 8) + (px[i].b << 16) + (px[i].a << 24);
                return h;
            }
        }

        static bool GraphicsFormatIsLinear(Texture2D tex)
        {
            var n = tex.name;
            return n.IndexOf("Normal", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Mask", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
