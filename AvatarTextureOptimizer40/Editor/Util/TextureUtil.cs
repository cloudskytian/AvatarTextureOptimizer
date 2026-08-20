using UnityEditor;
using UnityEngine;

namespace Fosa.Ato.Editor.Util
{
    internal static class TextureUtil
    {
        public static bool HasAlpha(Texture2D t)
        {
            if (t == null) return false;
            var path = AssetDatabase.GetAssetPath(t);
            if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter imp)
                return imp.DoesSourceTextureHaveAlpha();
            var f = t.format;
            return f == TextureFormat.RGBA32 || f == TextureFormat.ARGB32 || f == TextureFormat.RGBAHalf ||
                   f == TextureFormat.DXT5 || f == TextureFormat.BC7 || f == TextureFormat.ASTC_4x4 ||
                   f == TextureFormat.ASTC_6x6 || f == TextureFormat.ASTC_8x8;
        }

        /// <summary>True if all pixels are identical (solid color island). / 全部像素相同（纯色岛）。</summary>
        public static bool IsSolid(Color[] px, int w, int h, RectInt box)
        {
            if (px == null || box.width <= 1 || box.height <= 1) return true;
            Color first = px[box.yMin * w + box.xMin];
            for (int y = box.yMin; y < box.yMax; y++)
                for (int x = box.xMin; x < box.xMax; x++)
                {
                    var c = px[y * w + x];
                    if (Mathf.Abs(c.r - first.r) > 1 / 255f || Mathf.Abs(c.g - first.g) > 1 / 255f ||
                        Mathf.Abs(c.b - first.b) > 1 / 255f || Mathf.Abs(c.a - first.a) > 1 / 255f)
                        return false;
                }
            return true;
        }
    }
}
