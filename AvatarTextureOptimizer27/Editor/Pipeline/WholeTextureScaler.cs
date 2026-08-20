using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class WholeTextureScaler
    {
        public static List<AtlasResult> Apply(List<UvGroup> groups, AtoPlatformSettings settings, BakeReport report)
        {
            var results = new List<AtlasResult>();
            var seen = new HashSet<Texture2D>();
            foreach (var g in groups)
            {
                if (g.Whitelisted) continue;
                for (int i = 0; i < g.Textures.Count; i++)
                {
                    var tex = g.Textures[i];
                    if (tex == null || !seen.Add(tex)) continue;
                    float su = 1f, sv = 1f;
                    foreach (var isl in g.Islands)
                    {
                        su = Mathf.Min(su, isl.ScaleU);
                        sv = Mathf.Min(sv, isl.ScaleV);
                    }
                    int nw = Mathf.Max(4, Mathf.RoundToInt(tex.width * su));
                    int nh = Mathf.Max(4, Mathf.RoundToInt(tex.height * sv));
                    var copy = new Texture2D(nw, nh, TextureFormat.RGBA32, true, false);
                    copy.name = "ATO_" + tex.name;
                    if (tex.isReadable)
                    {
                        var px = tex.GetPixels();
                        var small = QualityMetrics.PremultipliedDownsample(px, tex.width, tex.height, nw, nh);
                        copy.SetPixels(small);
                    }
                    copy.Apply(true, false);
                    var ar = new AtlasResult { Atlas = copy, Semantic = i < g.Semantics.Count ? g.Semantics[i] : AtoTextureSemantic.Albedo };
                    ar.Sources.Add(tex);
                    results.Add(ar);
                    report.Details.Add($"Scale-only {tex.name} -> {nw}x{nh}");
                    AtoLog.Info($"Scale-only {tex.name} -> {nw}x{nh}");
                    // retarget later in MeshRewriter via Texture remap without UV change
                    g.Textures[i] = copy;
                }
            }
            return results;
        }
    }
}
