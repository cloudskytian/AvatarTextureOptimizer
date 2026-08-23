using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    internal static class TextureFingerprint
    {
        public static string Build(Texture2D texture)
        {
            var path = AssetDatabase.GetAssetPath(texture);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            var importerState = importer == null ? "<generated>" : EditorJsonUtility.ToJson(importer, true);
            var sampleState = string.Concat(texture.width, "x", texture.height, ";", texture.format, ";",
                texture.graphicsFormat, ";mips=", texture.mipmapCount, ";", texture.filterMode, ";",
                texture.wrapModeU, ";", texture.wrapModeV, ";", texture.wrapModeW, ";aniso=", texture.anisoLevel,
                ";bias=", texture.mipMapBias.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                ";streaming=", texture.streamingMipmaps, ";priority=", texture.streamingMipmapsPriority);
            var text = texture.imageContentsHash + "\n" + importerState + "\n" + sampleState;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var value in hash) builder.Append(value.ToString("x2"));
                return builder.ToString();
            }
        }

        public static string ImportSettings(Texture2D texture)
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) as TextureImporter;
            return importer == null ? "generated:" + texture.format : EditorJsonUtility.ToJson(importer, true);
        }

        public static bool IsSrgb(Texture2D texture)
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) as TextureImporter;
            return importer != null ? importer.sRGBTexture : GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
        }
    }
}
