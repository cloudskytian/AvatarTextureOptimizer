using UnityEngine;

// Texture classification from decoded content (alpha presence, grayscale) with kind hints.
// 基于解码内容的贴图分类（是否带 alpha、是否灰度），结合种类提示。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class TextureClassifier
    {
        /// <summary>
        /// Classifies a texture into an atlas/compression bucket class.
        /// kind 提示优先（Normal 恒为法线）；否则按内容：带 alpha→ColorAlpha；灰度→Mask；否则 ColorOpaque。
        /// The kind hint wins (a normal map is always a normal map); otherwise content decides:
        /// has alpha -> ColorAlpha; grayscale -> Mask; else ColorOpaque.
        /// </summary>
        public static TextureClass Classify(Texture2D tex, TextureKind kind, TextureDecodeCache decode)
        {
            if (kind == TextureKind.Normal) return TextureClass.Normal;
            var entry = decode.Get(tex);
            if (entry.HasAlpha) return TextureClass.ColorAlpha;
            if (entry.IsGrayscale) return TextureClass.Mask;
            return TextureClass.ColorOpaque;
        }
    }
}
