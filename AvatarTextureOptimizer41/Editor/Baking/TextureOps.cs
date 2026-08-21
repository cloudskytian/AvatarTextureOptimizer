using UnityEngine;

// GPU texture operations: UV-transformed bilinear resampling (linear space) and premultiplied-alpha
// resampling, used by quality evaluation and atlas baking.
// GPU 贴图操作：UV 变换双线性重采样（线性空间）与预乘 alpha 重采样，供质量评估与图集烘焙使用。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class TextureOps
    {
        private static Material _blitMat;
        private static int _uvScaleId = Shader.PropertyToID("_UVScale");
        private static int _uvOffsetId = Shader.PropertyToID("_UVOffset");

        private static Material BlitMat
        {
            get
            {
                if (_blitMat == null)
                {
                    var shader = Shader.Find("Hidden/ATO/Blit");
                    _blitMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                }
                return _blitMat;
            }
        }

        /// <summary>
        /// Resamples the given UV rect of a source texture into a float RGBA buffer of size (w,h).
        /// Sampling is bilinear; the output buffer holds linear-space values (the caller converts if sRGB).
        /// 将源贴图的指定 UV 矩形重采样为 (w,h) 的 float RGBA 缓冲。双线性采样；输出为线性空间值。
        /// </summary>
        public static float[] SampleRegion(Texture2D src, Rect uvRect, int w, int h, bool premultiplyAlpha, RenderTexturePool pool)
        {
            var rt = pool.Acquire(w, h, RenderTextureFormat.ARGB32, linear: true);
            var mat = BlitMat;
            // UV transform: rt.uv maps [0,1] onto the source rect. rt.uv 映射到源矩形。
            mat.SetVector(_uvScaleId, new Vector4(uvRect.width, uvRect.height, 0, 0));
            mat.SetVector(_uvOffsetId, new Vector4(uvRect.xMin, uvRect.yMin, 0, 0));
            Graphics.Blit(src, rt, mat, premultiplyAlpha ? 1 : 0);

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            tex.Apply(false, true);
            var px = tex.GetPixels32();
            UnityEngine.Object.DestroyImmediate(tex);
            RenderTexture.active = prev;
            pool.Release(rt);

            var buf = new float[w * h * 4];
            for (int i = 0; i < px.Length; i++)
            {
                int o = i * 4;
                buf[o] = px[i].r / 255f; buf[o + 1] = px[i].g / 255f; buf[o + 2] = px[i].b / 255f; buf[o + 3] = px[i].a / 255f;
            }
            return buf;
        }
    }
}
