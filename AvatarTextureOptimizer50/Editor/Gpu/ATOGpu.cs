// -----------------------------------------------------------------------------
// ATOGpu.cs — GPU utilities: raw pixel readback & pull-push bleed pyramid.
// ATOGpu.cs — GPU 工具：原始像素读回与 pull-push 渗色金字塔。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato.editor
{
    internal static class ATOGpu
    {
        private static Material _blitMat;

        /// <summary>Material wrapping ATOGpu.shader / 包装 ATOGpu.shader 的材质。</summary>
        private static Material Mat
        {
            get
            {
                if (_blitMat == null)
                {
                    var shader = Shader.Find("Hidden/ATO/Gpu");
                    if (shader == null)
                    {
                        ATOLog.Error("Hidden/ATO/Gpu shader not found — GPU features degraded");
                        return null;
                    }

                    _blitMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                }

                return _blitMat;
            }
        }

        /// <summary>
        /// Read a texture's pixels as raw (as-authored) Color32 regardless of Read/Write.
        /// sRGB→linear conversion is NOT applied here; call ToLinear if needed.
        /// 无论贴图是否可读，读回其原始（as-authored）Color32 像素。此处不做 sRGB→线性
        /// 转换；需要时调用 ToLinear。
        /// </summary>
        public static Color32[] ReadPixelsRaw(Texture2D tex, ATOGpuPool pool)
        {
            if (tex == null) return Array.Empty<Color32>();

            if (tex.isReadable)
            {
                try { return tex.GetPixels32(); }
                catch (Exception) { /* fall through to GPU / 转GPU路径 */ }
            }

            // GPU path: blit with GL.sRGBWrite so no color conversion happens in either
            // Linear or Gamma projects, then ReadPixels.
            // GPU 路径：GL.sRGBWrite 禁止任何颜色转换（Linear/Gamma 工程皆然），再 ReadPixels。
            var prevRT = RenderTexture.active;
            Color32[] result;
            try
            {
                var rt = pool.GetRT(tex.width, tex.height, RenderTextureFormat.ARGB32, linear: true);
                var prevSrgb = GL.sRGBWrite;
                GL.sRGBWrite = true; // write raw values untouched / 原样写入不转换
                Graphics.Blit(tex, rt);
                GL.sRGBWrite = prevSrgb;

                RenderTexture.active = rt;
                var readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0, false);
                readable.Apply(false, false);
                result = readable.GetPixels32();
                UnityEngine.Object.DestroyImmediate(readable);
            }
            finally
            {
                RenderTexture.active = prevRT;
            }

            return result;
        }

        /// <summary>Fill a PixelBuffer in LINEAR space from raw pixels.
        /// 由原始像素生成线性空间 PixelBuffer。</summary>
        public static PixelBuffer ToLinearBuffer(Color32[] raw, int w, int h, bool sourceIsSRGB)
        {
            if (sourceIsSRGB)
            {
                for (int i = 0; i < raw.Length; i++)
                {
                    raw[i].r = SrgbToLinear8(raw[i].r);
                    raw[i].g = SrgbToLinear8(raw[i].g);
                    raw[i].b = SrgbToLinear8(raw[i].b);
                }
            }

            return new PixelBuffer { pixels = raw, width = w, height = h };
        }

        private static byte SrgbToLinear8(byte v)
        {
            float c = v / 255f;
            float l = c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
            return (byte)Mathf.RoundToInt(Mathf.Clamp01(l) * 255f);
        }

        private static byte LinearToSrgb8(byte v)
        {
            float c = v / 255f;
            float s = c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
            return (byte)Mathf.RoundToInt(Mathf.Clamp01(s) * 255f);
        }

        public static Color32[] LinearToSrgb(Color32[] linear)
        {
            var outp = new Color32[linear.Length];
            for (int i = 0; i < linear.Length; i++)
            {
                outp[i] = linear[i];
                outp[i].r = LinearToSrgb8(linear[i].r);
                outp[i].g = LinearToSrgb8(linear[i].g);
                outp[i].b = LinearToSrgb8(linear[i].b);
            }

            return outp;
        }

        // ------------------------------------------------------------------ //
        // Pull-push bleed (infinite-ish dilation) / Pull-push 渗色（近似无限外扩）
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Dilate island colors into empty atlas regions using a pull-push pyramid (GPU).
        /// Empty pixels are alpha==0 pixels. After bleeding, original pixels are restored
        /// and empty pixels get bled RGB with alpha 0 (transparent atlases keep alpha 0;
        /// opaque atlases have no empty interior except padding anyway).
        /// 用 pull-push 金字塔（GPU）将岛颜色外扩到图集空白区域。空白=alpha==0 像素。
        /// 渗色后恢复原像素；空白处得到渗色 RGB 且 alpha 为 0。
        /// </summary>
        public static void PullPushBleed(Texture2D target, ATOGpuPool pool)
        {
            var mat = Mat;
            if (mat == null) return;

            int w = target.width, h = target.height;
            if (w < 2 || h < 2) return;
            var prev = RenderTexture.active;

            try
            {
                var levels = new List<RenderTexture>();
                var rt0 = pool.GetRT(w, h);
                var prevSrgb = GL.sRGBWrite;
                GL.sRGBWrite = true; // raw copy, no conversion / 原样拷贝不转换
                Graphics.Blit(target, rt0);
                GL.sRGBWrite = prevSrgb;
                levels.Add(rt0);

                // Pull chain / pull 链
                int lw = w, lh = h;
                while (lw > 1 || lh > 1)
                {
                    lw = Mathf.Max(1, lw / 2);
                    lh = Mathf.Max(1, lh / 2);
                    var src = levels[levels.Count - 1];
                    var dst = pool.GetRT(lw, lh);
                    Graphics.Blit(src, dst, mat, 0);
                    levels.Add(dst);
                }

                // Push chain: fill only empty pixels of each finer level.
                // push 链：仅填充更细层的空白像素。
                for (int i = levels.Count - 2; i >= 0; i--)
                {
                    var coarse = levels[i + 1];
                    var fine = levels[i];
                    var tmp = pool.GetRT(fine.width, fine.height);
                    mat.SetTexture("_OwnTex", fine);
                    Graphics.Blit(coarse, tmp, mat, 1);
                    // copy result back into the fine slot so the chain continues upward
                    // 将结果拷回 fine 槽位，使链条继续向上
                    Graphics.Blit(tmp, fine);
                }

                // Read back & compose on CPU: original pixels win; bled RGB elsewhere (alpha 0).
                // CPU 读回合成：原像素优先；其余取渗色RGB（alpha 0）。
                RenderTexture.active = levels[0];
                var readable = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                readable.Apply(false, false);
                var bled = readable.GetPixels32();
                UnityEngine.Object.DestroyImmediate(readable);

                var orig = target.GetPixels32();
                for (int i = 0; i < bled.Length; i++)
                {
                    if (orig[i].a == 0)
                        orig[i] = new Color32(bled[i].r, bled[i].g, bled[i].b, 0);
                }

                target.SetPixels32(orig);
                target.Apply(false, false);
            }
            finally
            {
                RenderTexture.active = prev;
            }
        }
    }
}
