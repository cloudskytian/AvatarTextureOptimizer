// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Textures/TextureDecodeCache.cs — 贴图解码缓存与 GPU 重采样 / Texture decode cache & GPU resampling
//
// 需求: 合理做缓存，避免不必要的重复解码；充分利用 GPU(RenderTexture)；不产生内存泄漏。
// 实现 (Coder1/Coder2 共识):
//  - 每张贴图解码一次为可读 RGBA32 副本（保留源导入色彩空间标记），缓存在本次构建内。
//  - GPU 双线性重采样（线性空间）；失败时回退 CPU 双线性（保证任何环境正确）。
//  - 所有 RenderTexture 生命周期 try/finally 释放。
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 贴图解码缓存 / Texture decode cache (per build).
    /// </summary>
    public sealed class TextureDecodeCache : IDisposable
    {
        private readonly Dictionary<Texture2D, Texture2D> _copies = new Dictionary<Texture2D, Texture2D>();
        private readonly Dictionary<Texture2D, bool> _hasAlpha = new Dictionary<Texture2D, bool>();
        private readonly Dictionary<Texture2D, Color32[]> _raw = new Dictionary<Texture2D, Color32[]>();
        private bool _disposed;

        /// <summary>
        /// 获取可读 RGBA32 副本（保留源色彩空间标记；sRGB 源 → sRGB 标记副本）/
        /// Get a readable RGBA32 copy preserving the source colorspace flag.
        /// </summary>
        public Texture2D GetCopy(Texture2D src, bool srgb)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TextureDecodeCache));
            if (_copies.TryGetValue(src, out var c)) return c;

            Texture2D copy;
            if (src.isReadable)
            {
                var pixels = src.GetPixels32();
                copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, linear: !srgb);
                copy.SetPixels32(pixels);
                copy.Apply(false, false);
            }
            else
            {
                copy = GPUResampler.ReadbackCopy(src, srgb);
            }

            copy.hideFlags = HideFlags.HideAndDontSave;
            _copies[src] = copy;
            return copy;
        }

        /// <summary>
        /// 原始像素（Color32 数组，含 alpha；与源存储值一致）/
        /// Raw pixels (Color32 array, matches source stored values).
        /// </summary>
        public Color32[] GetRawPixels(Texture2D src, bool srgb)
        {
            if (_raw.TryGetValue(src, out var p)) return p;
            p = GetCopy(src, srgb).GetPixels32();
            _raw[src] = p;
            return p;
        }

        /// <summary>
        /// 是否使用 alpha 通道（存在 <255 像素即视为使用）/
        /// Whether the alpha channel is used (any pixel with alpha &lt; 255).
        /// </summary>
        public bool UsesAlpha(Texture2D src, bool srgb)
        {
            if (_hasAlpha.TryGetValue(src, out var a)) return a;
            var pixels = GetRawPixels(src, srgb);
            bool uses = false;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a < 255) { uses = true; break; }
            }
            _hasAlpha[src] = uses;
            return uses;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var kv in _copies)
            {
                if (kv.Value != null) UnityEngine.Object.DestroyImmediate(kv.Value);
            }
            _copies.Clear();
            _raw.Clear();
            _hasAlpha.Clear();
        }
    }

    /// <summary>
    /// GPU 重采样工具（RenderTexture 双线性；CPU 兜底）/
    /// GPU resampling utility (RenderTexture bilinear; CPU fallback).
    /// </summary>
    public static class GPUResampler
    {
        // 全局锁：RenderTexture.active 等图形状态非线程安全 /
        // Global lock: graphics state (active RT etc.) is not thread-safe.
        public static readonly object GraphicsLock = new object();

        /// <summary>
        /// 将纹理读回为可读 RGBA32 副本（保留源色彩空间语义）/
        /// Read back a texture into a readable RGBA32 copy preserving colorspace semantics.
        /// </summary>
        public static Texture2D ReadbackCopy(Texture2D src, bool srgb)
        {
            if (src == null) return null;
            lock (GraphicsLock)
            {
                var previousActive = RenderTexture.active;
                var previousSRGB = GL.sRGBWrite;
                RenderTexture rt = null;
                try
                {
                    rt = new RenderTexture(src.width, src.height, 0, RenderTextureFormat.ARGB32,
                        srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                    rt.Create();
                    GL.sRGBWrite = srgb;
                    Graphics.Blit(src, rt);
                    var copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, linear: !srgb);
                    RenderTexture.active = rt;
                    copy.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                    copy.Apply(false, false);
                    copy.hideFlags = HideFlags.HideAndDontSave;
                    return copy;
                }
                finally
                {
                    GL.sRGBWrite = previousSRGB;
                    RenderTexture.active = previousActive;
                    if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                }
            }
        }

        /// <summary>
        /// 双线性重采样到目标尺寸（线性空间；sRGB 源自动转换）→ 返回线性值 Texture2D（或 null 失败）。
        /// Bilinear resample to target size (linear space; sRGB source converted) → linear-valued Texture2D.
        /// </summary>
        public static Texture2D ResampleLinear(Texture2D src, int outW, int outH, bool srgb)
        {
            if (src == null || outW <= 0 || outH <= 0) return null;
            if (src.width == outW && src.height == outH)
            {
                // 无需缩放：仍走一次线性化（保持一致语义） / Same size: still linearize for consistent semantics
            }

            lock (GraphicsLock)
            {
                var previousActive = RenderTexture.active;
                var previousSRGB = GL.sRGBWrite;
                RenderTexture rt = null, rtOut = null;
                try
                {
                    rt = new RenderTexture(outW, outH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    rt.Create();
                    GL.sRGBWrite = false; // 目标为线性 RT, 不编码 / linear RT: no encode
                    Graphics.Blit(src, rt); // sRGB 源在采样时自动线性化 / sRGB sources linearize on sample
                    var result = new Texture2D(outW, outH, TextureFormat.RGBA32, false, linear: true);
                    RenderTexture.active = rt;
                    result.ReadPixels(new Rect(0, 0, outW, outH), 0, 0, false);
                    result.Apply(false, false);
                    result.hideFlags = HideFlags.HideAndDontSave;
                    return result;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    GL.sRGBWrite = previousSRGB;
                    RenderTexture.active = previousActive;
                    if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                    if (rtOut != null) { rtOut.Release(); UnityEngine.Object.DestroyImmediate(rtOut); }
                }
            }
        }

        /// <summary>
        /// CPU 双线性重采样兜底（输入/输出均为线性 float RGB + alpha）/
        /// CPU bilinear fallback (linear float RGBA in/out).
        /// </summary>
        public static void ResampleLinearCPU(float[] srcRgba, int srcW, int srcH, float[] dst, int dstW, int dstH)
        {
            for (int y = 0; y < dstH; y++)
            {
                float sy = (y + 0.5f) * srcH / dstH - 0.5f;
                int y0 = Mathf.Clamp((int)Mathf.Floor(sy), 0, srcH - 1);
                int y1 = Mathf.Clamp(y0 + 1, 0, srcH - 1);
                float fy = sy - y0;
                for (int x = 0; x < dstW; x++)
                {
                    float sx = (x + 0.5f) * srcW / dstW - 0.5f;
                    int x0 = Mathf.Clamp((int)Mathf.Floor(sx), 0, srcW - 1);
                    int x1 = Mathf.Clamp(x0 + 1, 0, srcW - 1);
                    float fx = sx - x0;
                    int o = (y * dstW + x) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        float v = srcRgba[(y0 * srcW + x0) * 4 + c] * (1 - fx) * (1 - fy)
                                + srcRgba[(y0 * srcW + x1) * 4 + c] * fx * (1 - fy)
                                + srcRgba[(y1 * srcW + x0) * 4 + c] * (1 - fx) * fy
                                + srcRgba[(y1 * srcW + x1) * 4 + c] * fx * fy;
                        dst[o + c] = v;
                    }
                }
            }
        }
    }
}
