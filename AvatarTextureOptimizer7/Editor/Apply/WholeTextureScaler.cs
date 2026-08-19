using System.Collections.Generic;
using Fosa.AvatarTextureOptimizer;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// When atlas is off (or a UV group failed atlas): scale the whole Texture2D.
    /// 不生成图集（或 UV 组图集失败）时：整图缩放。
    /// </summary>
    public static class WholeTextureScaler
    {
        public static Texture2D Scale(AtoSession session, Texture2D src, int w, int h, AtoTextureKind kind, bool srgb)
        {
            if (src == null) return null;
            if (session.Lossless || (w >= src.width && h >= src.height))
                return src;

            var dec = session.DecodeCache.Get(src, kind == AtoTextureKind.Normal);
            var scaled = QualityEvaluator.Resample(dec.Linear, dec.Width, dec.Height, w, h,
                kind == AtoTextureKind.Albedo);
            if (kind == AtoTextureKind.Normal)
            {
                for (int i = 0; i < scaled.Length; i++)
                {
                    var n = new Vector3(scaled[i].r, scaled[i].g, scaled[i].b).normalized;
                    scaled[i] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                }
            }

            var linear = kind != AtoTextureKind.Albedo;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, linear)
            {
                name = src.name + "_ATO",
                filterMode = src.filterMode,
                wrapMode = src.wrapMode,
                anisoLevel = src.anisoLevel
            };
            tex.SetPixels(scaled);
            tex.Apply(false, false);
            session.Track(tex);
            session.Save(tex);
            session.Report.OutputPixels += (long)w * h;
            return tex;
        }

        public static void ScaleNonAtlas(AtoSession session, AtoGraph graph, AtlasPlan plan)
        {
            var done = new HashSet<Texture2D>();
            foreach (var ug in graph.UvGroups)
            {
                var noAtlas = !session.GenerateAtlas || plan.FailedAtlas.Contains(ug) || ug.SkipAtlas;
                if (!noAtlas) continue;
                foreach (var b in ug.Bindings)
                {
                    var src = b.Slot?.Texture;
                    if (src == null || done.Contains(src)) continue;
                    if (session.WhitelistTextures.Contains(src)) continue;
                    done.Add(src);

                    int w = src.width, h = src.height;
                    if (!session.Lossless)
                    {
                        // Use the largest scaled island ratio, else binary-search the whole image.
                        // 用最大已缩放岛比例，否则对整图二分。
                        float su = 1f, sv = 1f;
                        foreach (var isl in ug.Islands)
                        {
                            su = Mathf.Min(su, isl.ScaleU > 0 ? isl.ScaleU : 1f);
                            sv = Mathf.Min(sv, isl.ScaleV > 0 ? isl.ScaleV : 1f);
                        }

                        w = Mathf.Max(1, Mathf.RoundToInt(src.width * su));
                        h = Mathf.Max(1, Mathf.RoundToInt(src.height * sv));
                    }

                    var ntex = Scale(session, src, w, h, b.Slot.Kind, b.Slot.IsSrgb);
                    if (ntex != null && ntex != src)
                    {
                        session.TextureRemap[src] = ntex;
                    }

                    session.Report.SourcePixels += (long)src.width * src.height;
                }
            }
        }
    }
}
