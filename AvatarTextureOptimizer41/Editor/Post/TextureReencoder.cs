using UnityEngine;

// Whole-texture rescaling for non-atlased textures (GPU bilinear downsample, premultiplied alpha aware).
// 非图集化贴图的整图缩放（GPU 双线性下采样，预乘 alpha 感知）。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class TextureReencoder
    {
        /// <summary>
        /// Downsamples a texture to a new Texture2D (RGBA32). Never upscales.
        /// 将贴图下采样为新 Texture2D（RGBA32）。永不上采样。
        /// </summary>
        public static Texture2D ScaleWhole(Texture2D src, int newW, int newH, bool premultiplyAlpha, RenderTexturePool pool)
        {
            newW = Mathf.Max(1, Mathf.Min(newW, src.width));
            newH = Mathf.Max(1, Mathf.Min(newH, src.height));
            if (newW == src.width && newH == src.height) return null;

            var rt = pool.Acquire(newW, newH, RenderTextureFormat.ARGB32, linear: true);
            var mat = new Material(Shader.Find("Hidden/ATO/Blit")) { hideFlags = HideFlags.HideAndDontSave };
            Graphics.Blit(src, rt, mat, premultiplyAlpha ? 1 : 0);
            UnityEngine.Object.DestroyImmediate(mat);

            var tex = new Texture2D(newW, newH, TextureFormat.RGBA32, false) { name = "ATO_" + src.name + "_scaled" };
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, newW, newH), 0, 0, false);
            tex.Apply(false, true);
            RenderTexture.active = prev;
            pool.Release(rt);
            return tex;
        }
    }
}
