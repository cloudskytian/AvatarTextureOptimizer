using System;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// 4 px granularity island rasterizer (CPU; Burst-friendly loops).
    /// 4 像素粒度的岛光栅化（CPU；循环可 Burst）。
    /// </summary>
    internal static class ATORasterizer
    {
        public const int Granularity = 4;

        public static ATOBitmask Rasterize(ATOIsland island, int pixelW, int pixelH)
        {
            var mw = Math.Max(1, (pixelW + Granularity - 1) / Granularity);
            var mh = Math.Max(1, (pixelH + Granularity - 1) / Granularity);
            var mask = ATOBitmask.Allocate(mw, mh);
            if (island.Renderer == null || island.Renderer.Mesh == null) return mask;

            var mesh = island.Renderer.Mesh;
            var uvs = ATOIslandExtractor.GetUv(mesh, island.UvChannel);
            if (uvs == null) return mask;
            var tris = mesh.GetTriangles(island.Submesh);

            foreach (var t in island.TriangleIndices)
            {
                if (t * 3 + 2 >= tris.Length) continue;
                var i0 = tris[t * 3];
                var i1 = tris[t * 3 + 1];
                var i2 = tris[t * 3 + 2];
                var a = UvToMask(uvs[i0], island, mw, mh);
                var b = UvToMask(uvs[i1], island, mw, mh);
                var c = UvToMask(uvs[i2], island, mw, mh);
                FillTriangle(mask, a, b, c);
            }
            return mask;
        }

        private static Vector2 UvToMask(Vector2 uv, ATOIsland island, int mw, int mh)
        {
            var size = island.UvSize;
            var u = size.x > 1e-8f ? (uv.x - island.UvMin.x) / size.x : 0f;
            var v = size.y > 1e-8f ? (uv.y - island.UvMin.y) / size.y : 0f;
            return new Vector2(u * (mw - 0.001f), v * (mh - 0.001f));
        }

        private static void FillTriangle(ATOBitmask mask, Vector2 a, Vector2 b, Vector2 c)
        {
            var minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), 0, mask.Width - 1);
            var maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))), 0, mask.Width - 1);
            var minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))), 0, mask.Height - 1);
            var maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))), 0, mask.Height - 1);
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                if (PointInTriangle(x + 0.5f, y + 0.5f, a, b, c))
                    mask[x, y] = true;
            }
        }

        private static bool PointInTriangle(float px, float py, Vector2 a, Vector2 b, Vector2 c)
        {
            var v0x = c.x - a.x; var v0y = c.y - a.y;
            var v1x = b.x - a.x; var v1y = b.y - a.y;
            var v2x = px - a.x; var v2y = py - a.y;
            var dot00 = v0x * v0x + v0y * v0y;
            var dot01 = v0x * v1x + v0y * v1y;
            var dot02 = v0x * v2x + v0y * v2y;
            var dot11 = v1x * v1x + v1y * v1y;
            var dot12 = v1x * v2x + v1y * v2y;
            var inv = dot00 * dot11 - dot01 * dot01;
            if (Mathf.Abs(inv) < 1e-12f) return false;
            inv = 1f / inv;
            var u = (dot11 * dot02 - dot01 * dot12) * inv;
            var v = (dot00 * dot12 - dot01 * dot02) * inv;
            return u >= -1e-4f && v >= -1e-4f && (u + v) <= 1.0001f;
        }
    }
}
