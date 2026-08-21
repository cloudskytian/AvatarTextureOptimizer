using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Rasterizes UV islands into cropped texture patches.
    /// 将 UV 岛光栅化到裁剪后的贴图片段中。
    /// </summary>
    internal static class AtoTextureRasterizer
    {
        public static Color[] RenderUvGroupPatch(Texture2D readableSource, AtoUvGroupRecord uvGroup, int width, int height, AtoTextureSemantic semantic)
        {
            var colors = new Color[width * height];
            var clear = GetClearColor(semantic);
            for (var i = 0; i < colors.Length; i++)
            {
                colors[i] = clear;
            }

            if (readableSource == null || uvGroup == null || uvGroup.Islands.Count == 0)
            {
                return colors;
            }

            var translation = uvGroup.InUnitSquareAlready ? Vector2.zero : uvGroup.Translation;
            var min = uvGroup.Min + translation;
            var span = uvGroup.Span;
            var spanX = Mathf.Max(span.x, 0.000001f);
            var spanY = Mathf.Max(span.y, 0.000001f);

            foreach (var island in uvGroup.Islands)
            {
                foreach (var triangle in island.Triangles)
                {
                    var aUv = triangle.A + translation;
                    var bUv = triangle.B + translation;
                    var cUv = triangle.C + translation;

                    var a = ToPixelSpace(aUv, min, spanX, spanY, width, height);
                    var b = ToPixelSpace(bUv, min, spanX, spanY, width, height);
                    var c = ToPixelSpace(cUv, min, spanX, spanY, width, height);

                    var area = Edge(a, b, c);
                    if (Mathf.Abs(area) < 0.000001f)
                    {
                        continue;
                    }

                    var minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), 0, width - 1);
                    var maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))), 0, width - 1);
                    var minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))), 0, height - 1);
                    var maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))), 0, height - 1);

                    for (var y = minY; y <= maxY; y++)
                    {
                        var py = y + 0.5f;
                        for (var x = minX; x <= maxX; x++)
                        {
                            var p = new Vector2(x + 0.5f, py);
                            var w0 = Edge(b, c, p);
                            var w1 = Edge(c, a, p);
                            var w2 = Edge(a, b, p);
                            var sameSign = area > 0.0f
                                ? (w0 >= 0.0f && w1 >= 0.0f && w2 >= 0.0f)
                                : (w0 <= 0.0f && w1 <= 0.0f && w2 <= 0.0f);
                            if (!sameSign)
                            {
                                continue;
                            }

                            w0 /= area;
                            w1 /= area;
                            w2 /= area;
                            var sourceUv = aUv * w0 + bUv * w1 + cUv * w2;
                            colors[y * width + x] = readableSource.GetPixelBilinear(Mathf.Clamp01(sourceUv.x), Mathf.Clamp01(sourceUv.y));
                        }
                    }
                }
            }

            return colors;
        }

        private static Vector2 ToPixelSpace(Vector2 uv, Vector2 min, float spanX, float spanY, int width, int height)
        {
            var nx = (uv.x - min.x) / spanX;
            var ny = (uv.y - min.y) / spanY;
            return new Vector2(nx * width, ny * height);
        }

        private static float Edge(Vector2 a, Vector2 b, Vector2 c)
        {
            return (c.x - a.x) * (b.y - a.y) - (c.y - a.y) * (b.x - a.x);
        }

        private static Color GetClearColor(AtoTextureSemantic semantic)
        {
            return semantic switch
            {
                AtoTextureSemantic.Normal => new Color(0.5f, 0.5f, 1.0f, 1.0f),
                AtoTextureSemantic.Grayscale => new Color(0.0f, 0.0f, 0.0f, 1.0f),
                _ => Color.clear,
            };
        }
    }
}
