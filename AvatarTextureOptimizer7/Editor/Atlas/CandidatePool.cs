using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    public readonly struct AtlasCandidate
    {
        public readonly int Width;
        public readonly int Height;
        public readonly int Area;
        public readonly float Aspect; // long/short, 1 = square

        public AtlasCandidate(int w, int h)
        {
            Width = w;
            Height = h;
            Area = w * h;
            Aspect = w >= h ? (float)w / h : (float)h / w;
        }
    }

    public static class CandidatePool
    {
        public static List<AtlasCandidate> Build(int min, int max, bool npot)
        {
            var list = new List<AtlasCandidate>();
            if (npot)
            {
                for (int w = min; w <= max; w += 64)
                for (int h = min; h <= max; h += 64)
                    list.Add(new AtlasCandidate(w, h));
            }
            else
            {
                for (int w = min; w <= max; w <<= 1)
                for (int h = min; h <= max; h <<= 1)
                    list.Add(new AtlasCandidate(w, h));
            }

            // Area asc, then aspect asc (square first). / 面积升序，然后长宽比升序（越方越先）。
            list.Sort((a, b) =>
            {
                var c = a.Area.CompareTo(b.Area);
                return c != 0 ? c : a.Aspect.CompareTo(b.Aspect);
            });
            return list;
        }

        public static int PaddingFor(int maxEdge, int minPadding)
        {
            var p = Mathf.CeilToInt(maxEdge / 128f);
            if (p < minPadding) p = minPadding;
            return Mathf.Max(4, p);
        }
    }
}
