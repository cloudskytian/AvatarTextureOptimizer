// English: GPU resample via RenderTexture (linear / premultiplied). Metrics stay on CPU after readback.
// 中文：用 RenderTexture 做线性/预乘下采样。指标在回读后仍由 CPU 计算（与 CPU 路径同一套阈值）。
using System;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class AtoGpuQuality
    {
        public static bool TryDownsampleGpu(Color32[] src, int sw, int sh, int dw, int dh, bool linear, bool premul,
            out Color32[] dst)
        {
            dst = null;
            if (sw < 1 || sh < 1 || dw < 1 || dh < 1) return false;
            RenderTexture srcRt = null, dstRt = null;
            Texture2D srcTex = null, read = null;
            var prev = RenderTexture.active;
            try
            {
                srcTex = new Texture2D(sw, sh, TextureFormat.RGBA32, false, linear);
                srcTex.SetPixels32(src);
                srcTex.Apply(false, false);
                var rw = linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;
                srcRt = RenderTexture.GetTemporary(sw, sh, 0, RenderTextureFormat.ARGB32, rw);
                dstRt = RenderTexture.GetTemporary(dw, dh, 0, RenderTextureFormat.ARGB32, rw);
                Graphics.Blit(srcTex, srcRt);
                Graphics.Blit(srcRt, dstRt);
                RenderTexture.active = dstRt;
                read = new Texture2D(dw, dh, TextureFormat.RGBA32, false, linear);
                read.ReadPixels(new Rect(0, 0, dw, dh), 0, 0);
                read.Apply(false, false);
                dst = read.GetPixels32();
                return dst != null && dst.Length == dw * dh;
            }
            catch (Exception e)
            {
                AtoLog.VerboseInfo("GPU resample fallback: " + e.Message);
                return false;
            }
            finally
            {
                RenderTexture.active = prev;
                if (srcRt) RenderTexture.ReleaseTemporary(srcRt);
                if (dstRt) RenderTexture.ReleaseTemporary(dstRt);
                if (srcTex) UnityEngine.Object.DestroyImmediate(srcTex);
                if (read) UnityEngine.Object.DestroyImmediate(read);
            }
        }
    }
}
