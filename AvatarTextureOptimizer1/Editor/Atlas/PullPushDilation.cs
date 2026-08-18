// PullPushDilation.cs / PullPushDilation.cs
// CPU/GPU pull-push dilation to bleed UV island colors into padding areas (reduces mipmap bleeding).
// CPU/GPU pull-push外扩，把UV岛颜色扩散到padding区域（减少mipmap渗色）。
// Transparent textures keep alpha=0; opaque textures bleed edge colors infinitely outward.
// 透明贴图保持alpha=0；不透明贴图无限向外扩散边缘颜色。

using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Atlas
{
    public static class PullPushDilation
    {
        /// <summary>
        /// Perform simple edge-bleed dilation on a texture by iteratively copying the nearest opaque/non-zero
        /// pixel into transparent/zero pixels in the padding regions. For production, a GPU pull-push shader
        /// would produce much better results; this is a conservative CPU fallback.
        /// 通过迭代地把最近的不透明/非零像素复制到padding区域的透明/零像素，进行简单边缘渗色外扩。
        /// 生产中应该用GPU pull-push着色器得到更好结果；这是保守CPU回退。
        /// </summary>
        public static void Dilate(Texture2D tex, int paddingPx, bool hasAlpha)
        {
            if (tex == null || paddingPx <= 0) return;
            int w = tex.width, h = tex.height;
            Color[] pixels = tex.GetPixels();

            // Mark which pixels are "filled" (island interior) vs. "empty" (padding background)
            // 标记哪些像素是"填充"的（岛内部），哪些是"空"的（padding背景）
            bool[] filled = new bool[w * h];
            for (int i = 0; i < pixels.Length; i++)
            {
                if (!hasAlpha) { filled[i] = true; continue; }
                filled[i] = pixels[i].a > 0.001f;
            }

            // Multi-pass dilate: spread filled pixels outward by copying neighbor colors
            // 多遍外扩：通过复制邻居颜色向外扩散填充像素
            Color[] src = (Color[])pixels.Clone();
            Color[] dst = (Color[])pixels.Clone();
            for (int pass = 0; pass < paddingPx + 2; pass++)
            {
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    if (filled[idx]) { dst[idx] = src[idx]; continue; }
                    // Sample 4 neighbors; average filled neighbors to produce an edge color
                    // 采样4邻居；平均已填充邻居产生边缘色
                    Color sum = Color.black;
                    int count = 0;
                    if (x > 0 && filled[idx - 1]) { sum += src[idx - 1]; count++; }
                    if (x < w-1 && filled[idx + 1]) { sum += src[idx + 1]; count++; }
                    if (y > 0 && filled[idx - w]) { sum += src[idx - w]; count++; }
                    if (y < h-1 && filled[idx + w]) { sum += src[idx + w]; count++; }
                    if (count > 0)
                    {
                        dst[idx] = sum / count;
                        if (hasAlpha) dst[idx].a = 0; // keep alpha 0 for padding on transparent maps
                        else dst[idx].a = 1;
                    }
                }
                // Mark newly filled pixels / 标记新填充的像素
                for (int i = 0; i < filled.Length; i++)
                {
                    if (!filled[i] && dst[i].a > 0.001f) filled[i] = true;
                }
                var tmp = src; src = dst; dst = tmp;
            }

            tex.SetPixels(src);
            tex.Apply(true, false);
        }
    }
}
