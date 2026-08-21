using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Lightweight post-process helpers for generated textures.
    /// 生成贴图的轻量后处理辅助工具。
    /// </summary>
    internal static class AtoTexturePostprocess
    {
        public static void DilateTransparentBorders(Texture2D texture, AtoTextureSemantic semantic, int iterations)
        {
            if (texture == null || iterations <= 0)
            {
                return;
            }

            var width = texture.width;
            var height = texture.height;
            var pixels = texture.GetPixels();
            var buffer = new Color[pixels.Length];
            var clear = GetClearColor(semantic);

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                System.Array.Copy(pixels, buffer, pixels.Length);
                var anyChanged = false;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var index = y * width + x;
                        if (!IsFillTarget(pixels[index], semantic, clear))
                        {
                            continue;
                        }

                        var sum = Color.black;
                        var count = 0;
                        for (var oy = -1; oy <= 1; oy++)
                        {
                            var ny = y + oy;
                            if (ny < 0 || ny >= height) continue;
                            for (var ox = -1; ox <= 1; ox++)
                            {
                                var nx = x + ox;
                                if (nx < 0 || nx >= width || (ox == 0 && oy == 0)) continue;
                                var neighbor = pixels[ny * width + nx];
                                if (IsSourcePixel(neighbor, semantic, clear))
                                {
                                    sum += neighbor;
                                    count++;
                                }
                            }
                        }

                        if (count <= 0)
                        {
                            continue;
                        }

                        var fill = sum / count;
                        if (semantic is AtoTextureSemantic.Color or AtoTextureSemantic.Mask)
                        {
                            fill.a = 0.0f;
                        }
                        buffer[index] = fill;
                        anyChanged = true;
                    }
                }

                pixels = buffer;
                buffer = new Color[pixels.Length];
                if (!anyChanged)
                {
                    break;
                }
            }

            texture.SetPixels(pixels);
        }

        public static void RenormalizeNormalMap(Texture2D texture)
        {
            if (texture == null)
            {
                return;
            }

            var pixels = texture.GetPixels();
            for (var i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                var n = new Vector3(c.r * 2.0f - 1.0f, c.g * 2.0f - 1.0f, c.b * 2.0f - 1.0f);
                if (n.sqrMagnitude < 0.000001f)
                {
                    n = Vector3.forward;
                }
                else
                {
                    n.Normalize();
                }

                pixels[i] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, c.a);
            }

            texture.SetPixels(pixels);
        }

        private static bool IsFillTarget(Color color, AtoTextureSemantic semantic, Color clear)
        {
            return semantic switch
            {
                AtoTextureSemantic.Color or AtoTextureSemantic.Mask => color.a <= 0.0001f,
                _ => Approximately(color, clear),
            };
        }

        private static bool IsSourcePixel(Color color, AtoTextureSemantic semantic, Color clear)
        {
            return semantic switch
            {
                AtoTextureSemantic.Color or AtoTextureSemantic.Mask => color.a > 0.0001f,
                _ => !Approximately(color, clear),
            };
        }

        private static bool Approximately(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.0001f
                   && Mathf.Abs(a.g - b.g) < 0.0001f
                   && Mathf.Abs(a.b - b.b) < 0.0001f
                   && Mathf.Abs(a.a - b.a) < 0.0001f;
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
