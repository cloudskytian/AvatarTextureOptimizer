using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// No-atlas path: scale whole textures, keep UVs. / 不生成图集：整图缩放，不重排 UV。
    /// </summary>
    public static class AtoWholeTextureScaler
    {
        public static void Apply(AtoGraph graph, List<AtoIsland> islands, AtoPlatformOverride settings,
            AtoTextureCache cache, AtoReport report)
        {
            var byTex = islands.Where(i => i.Eligible).GroupBy(i => i.Source);
            foreach (var g in byTex)
            {
                var tex = g.Key;
                float su = g.Max(i => i.ScaleU);
                float sv = g.Max(i => i.ScaleV);
                int nw = Mathf.Max(1, Mathf.RoundToInt(tex.width * su));
                int nh = Mathf.Max(1, Mathf.RoundToInt(tex.height * sv));
                if (nw == tex.width && nh == tex.height) continue;
                var px = cache.GetPixels(tex);
                var down = AtoQualityEval.BilinearDownsample(px, tex.width, tex.height, nw, nh, false);
                var nt = new Texture2D(nw, nh, TextureFormat.RGBA32, false, !tex.isDataSRGB)
                {
                    name = "ATO_" + tex.name,
                    filterMode = tex.filterMode,
                    wrapMode = tex.wrapMode,
                    anisoLevel = tex.anisoLevel
                };
                nt.SetPixels32(down);
                nt.Apply(false, false);
                Remap(graph, tex, nt);
                AtoApply.TextureRemap[tex] = nt;
                report.ResultTexels += (long)nw * nh;
                report.OriginalTexels += (long)tex.width * tex.height;
                report.Detail($"scale-only {tex.name} {tex.width}x{tex.height} -> {nw}x{nh}");
            }
        }

        static void Remap(AtoGraph graph, Texture2D from, Texture2D to)
        {
            foreach (var b in graph.Bindings)
            {
                if (b.Texture != from || b.Material == null) continue;
                if (b.Material.HasProperty(b.Property))
                    b.Material.SetTexture(b.Property, to);
                b.Texture = to;
            }
        }
    }
}
