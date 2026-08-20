// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System;
using AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Burst
{
    /// <summary>
    /// GPU-accelerated bilinear resampling via RenderTexture + Graphics.Blit. Used to
    /// scale island fragments in bulk during atlas construction. Bilinear filtering is
    /// hardware-accelerated; premultiplied-alpha correctness is preserved by operating in
    /// linear space and re-multiplying alpha on readback (see ATOTextureReader).
    ///
    /// 通过 RenderTexture + Graphics.Blit 的 GPU 双线性重采样，用于图集构建时批量缩放岛
    /// 碎片。双线性过滤为硬件加速；线性空间 + 回读时重新预乘 alpha 保证预乘正确性。
    /// </summary>
    public static class ATOGpuResampler
    {
        /// <summary>
        /// Bilinear-scale a texture region on the GPU and read back linear pixels.
        /// 在 GPU 上双线性缩放贴图区域并回读线性像素。
        /// </summary>
        public static Color[] BilinearScale(Texture2D src, int srcX, int srcY, int srcW, int srcH,
            int dstW, int dstH, bool linear)
        {
            if (src == null) return null;

            var rt = RenderTexture.GetTemporary(dstW, dstH, 0,
                RenderTextureFormat.ARGBFloat, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);

            // Blit the source region to the destination RT (bilinear by default).
            // 将源区域 Blit 到目标 RT（默认双线性）。
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, dstW, dstH, 0);
            Graphics.DrawTexture(
                new Rect(0, 0, dstW, dstH),
                src,
                new Rect((float)srcX / src.width, (float)srcY / src.height,
                    (float)srcW / src.width, (float)srcH / src.height),
                0, 0, 0, 0,
                null);
            GL.PopMatrix();
            RenderTexture.active = prev;

            var tex = new Texture2D(dstW, dstH, TextureFormat.RGBAFloat, false, linear);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            var pixels = tex.GetPixels();
            UnityEngine.Object.DestroyImmediate(tex);
            RenderTexture.ReleaseTemporary(rt);
            return pixels;
        }

        /// <summary>
        /// Whether GPU resampling should be preferred for this size of work.
        /// 该工作量是否优先使用 GPU 重采样。
        /// </summary>
        public static bool ShouldUseGpu(int pixelCount)
        {
            return ATOCompute.GpuAvailable && pixelCount >= 64 * 64;
        }
    }
}
