using System.Collections.Generic;
using UnityEngine;

namespace Fosa.Ato.Editor.Packing
{
    /// <summary>
    /// Builds the candidate atlas dimension pool. Default: power-of-two edges (64..max). Experimental
    /// NPOT: steps by 64 (64..max), and strips unsupported per-platform formats (e.g. iOS PVRTC).
    /// 候选图集尺寸池。默认 2 的幂（64..max）；NPOT 以 64 步进，并剔除平台不支持的格式（如 iOS PVRTC）。
    /// </summary>
    internal static class AtlasPool
    {
        public struct Dim { public int W, H; public long Area => (long)W * H; public float Ratio => (float)Mathf.Max(W, H) / Mathf.Min(W, H); }

        public static List<Dim> Build(int maxEdge, bool npot, Runtime.AtoPlatform platform)
        {
            var set = new HashSet<(int, int)>();
            void Add(int w, int h)
            {
                if (w < 64 || h < 64) return;
                if (w > maxEdge || h > maxEdge) return;
                if (w < h) (w, h) = (h, w); // canonical landscape / 规范为横向
                set.Add((w, h));
            }

            if (npot)
            {
                for (int w = 64; w <= maxEdge; w += 64)
                    for (int h = 64; h <= w; h += 64)
                    {
                        if (platform == Runtime.AtoPlatform.iOS && IsPvrtcOnlyPair(w, h)) continue;
                        Add(w, h);
                    }
            }
            else
            {
                for (int pw = 6; pw <= 20; pw++)
                    for (int ph = 6; ph <= pw; ph++)
                    {
                        int w = 1 << pw, h = 1 << ph;
                        Add(w, h);
                    }
            }

            var list = new List<Dim>();
            foreach (var (w, h) in set) list.Add(new Dim { W = w, H = h });
            // Sort area asc, then ratio asc (most square first) per spec.
            // 按面积升序、长宽比升序（越接近正方越优先）
            list.Sort((a, b) =>
            {
                int c = a.Area.CompareTo(b.Area);
                if (c != 0) return c;
                return a.Ratio.CompareTo(b.Ratio);
            });
            return list;
        }

        // PVRTC requires square power-of-two; NPOT or non-square must be stripped on iOS.
        // PVRTC 要求正方形且 2 的幂；iOS 上 NPOT/非正方形需剔除
        private static bool IsPvrtcOnlyPair(int w, int h) => w != h || (w & w - 1) != 0;
    }
}
