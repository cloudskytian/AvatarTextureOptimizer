using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// GPU helpers: raw readback (sRGB-correct), region resample (bilinear, linear-space,
    /// premultiplied-alpha aware), atlas composition quads, and pull-push bleed.
    ///
    /// Raw readback correctness: in Linear-space projects sampling an sRGB texture converts to
    /// linear, so we blit into an sRGB RenderTexture to get a round trip back to the raw stored
    /// bytes; linear textures blit into a linear RT. Gamma-space projects use linear RTs only.
    /// / GPU 工具：原始像素读回（sRGB 无损往返）、区域重采样（线性空间+预乘alpha）、
    /// 图集合成四边形、pull-push 渗色。读回：Linear 工程下 sRGB 贴图经 sRGB RT 往返还原原始字节。
    /// </summary>
    internal static class Gfx
    {
        internal static bool LinearProject => PlayerSettings.colorSpace == ColorSpace.Linear;

        /// <summary>Read raw stored bytes of a texture (GPU blit path for non-readable assets). / 读取贴图原始存储字节。</summary>
        internal static Color32[] ReadPixelsRaw(Texture2D tex, bool srgb)
        {
            var prev = RenderTexture.active;
            RenderTexture rt = null;
            try
            {
                var readWrite = srgb && LinearProject ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
                rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, readWrite);
                Graphics.Blit(tex, rt); // 1:1 copy, sampler conversions cancel via matching RT / 1:1 拷贝，往返转换相消
                RenderTexture.active = rt;
                var tmp = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, true);
                try
                {
                    tmp.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0, false);
                    return tmp.GetPixels32();
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(tmp);
                }
            }
            finally
            {
                if (rt != null)
                {
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
        }

        /// <summary>
        /// Create a temporary CPU-side texture from raw bytes. Sampling performs no sRGB conversion
        /// (linear=true) so shaders control conversions explicitly.
        /// / 由原始字节创建临时贴图（linear 采样，转换由着色器显式控制）。
        /// </summary>
        internal static Texture2D CreateTempTexture(int width, int height, Color32[] pixels,
            FilterMode filter = FilterMode.Bilinear)
        {
            var t = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
            {
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp,
                name = "ATO_TempTex",
            };
            t.SetPixels32(pixels);
            t.Apply(false, false);
            return t;
        }

        /// <summary>
        /// GPU bilinear resample of a rectangular region of `src` into a dstW×dstH Color32 buffer.
        /// `linearize` converts sRGB bytes to linear before filtering; `premultiply` multiplies rgb
        /// by alpha after linearizing (correct transparency downsample). Result stays linear.
        /// / GPU 双线性重采样源图一个矩形区域；linearize=先转线性，premultiply=预乘alpha；结果为线性值。
        /// </summary>
        internal static Color32[] ResampleRegion(Texture2D src, RectInt srcRect, int dstW, int dstH,
            bool linearize, bool premultiply, Material resampleMat)
        {
            var prev = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(dstW, dstH, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            try
            {
                Graphics.SetRenderTarget(rt);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, dstW, 0, dstH); // y=0 is bottom = row 0 / 底部为第0行，避免垂直翻转
                GL.Clear(false, true, Color.clear);

                var srcTex = (Texture)src;
                float su = 1f / src.width, sv = 1f / src.height;
                // half-texel inset for correct bilinear sampling of the region / 半像素内缩
                float u0 = (srcRect.x + 0.5f) * su, u1 = (srcRect.x + srcRect.width - 0.5f) * su;
                float v0 = (srcRect.y + 0.5f) * sv, v1 = (srcRect.y + srcRect.height - 0.5f) * sv;

                resampleMat.SetTexture("_MainTex", srcTex);
                SetKeyword(resampleMat, "ATO_LINEARIZE", linearize);
                SetKeyword(resampleMat, "ATO_PREMUL", premultiply);
                resampleMat.SetPass(0);

                GL.Begin(GL.QUADS);
                GL.TexCoord2(u0, v0); GL.Vertex3(0, 0, 0);
                GL.TexCoord2(u1, v0); GL.Vertex3(dstW, 0, 0);
                GL.TexCoord2(u1, v1); GL.Vertex3(dstW, dstH, 0);
                GL.TexCoord2(u0, v1); GL.Vertex3(0, dstW, 0);
                GL.End();
                GL.PopMatrix();

                RenderTexture.active = rt;
                var tmp = new Texture2D(dstW, dstH, TextureFormat.RGBA32, false, true);
                try
                {
                    tmp.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0, false);
                    return tmp.GetPixels32();
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(tmp);
                }
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>Bilinear upsample of a raw pixel buffer back to (dstW,dstH) with optional un-premultiply. / 双线性上采样回原尺寸（可选反预乘）。</summary>
        internal static Color32[] UpsampleBuffer(Color32[] srcPixels, int srcW, int srcH, int dstW, int dstH,
            bool unpremultiply, Material resampleMat)
        {
            using var temp = TempTextureScope(srcPixels, srcW, srcH);
            var outPixels = ResampleRegion(temp.Texture, new RectInt(0, 0, srcW, srcH), dstW, dstH, false, false, resampleMat);
            if (unpremultiply)
            {
                for (int i = 0; i < outPixels.Length; i++)
                {
                    var c = outPixels[i];
                    if (c.a > 0)
                    {
                        var f = 255f / c.a;
                        c.r = (byte)Mathf.Clamp(c.r * f, 0, 255);
                        c.g = (byte)Mathf.Clamp(c.g * f, 0, 255);
                        c.b = (byte)Mathf.Clamp(c.b * f, 0, 255);
                        outPixels[i] = c;
                    }
                }
            }
            return outPixels;
        }

        internal readonly struct TempTextureScope : IDisposable
        {
            internal readonly Texture2D Texture;
            internal TempTextureScope(Color32[] pixels, int w, int h)
            {
                Texture = CreateTempTexture(w, h, pixels);
            }
            public void Dispose() => UnityEngine.Object.DestroyImmediate(Texture);
        }

        internal static void SetKeyword(Material m, string kw, bool on)
        {
            if (on) m.EnableKeyword(kw);
            else m.DisableKeyword(kw);
        }
    }
}
