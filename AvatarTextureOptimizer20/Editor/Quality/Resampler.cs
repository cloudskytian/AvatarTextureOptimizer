// GPU (RenderTexture) resampling: linear-space, premultiplied-alpha downsampling, with
// bilinear round-trip back to original size for metric comparison.
// GPU 重采样：线性空间、透明预乘下采样、双线性回采到原尺寸用于指标对比。
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class Resampler
    {
        private static Material _mat;
        private static Material Mat
        {
            get
            {
                if (_mat == null)
                {
                    var sh = Shader.Find("Hidden/ATO/Resample");
                    _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                }
                return _mat;
            }
        }

        private static Material _decode;
        private static Material Decode
        {
            get
            {
                if (_decode == null)
                {
                    var sh = Shader.Find("Hidden/ATO/Decode");
                    _decode = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                }
                return _decode;
            }
        }

        private static RenderTexture NewRt(int w, int h) =>
            RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);

        /// <summary>
        /// Crop rect from tex, downsample to small (premultiplied if requested), upsample back
        /// to rect size, return pixels. Caller disposes the array.
        /// 裁剪→下采样（可预乘）→回采→返回像素；调用方负责 Dispose。
        /// </summary>
        public static NativeArray<float4> RoundTrip(Texture2D tex, RectInt rect, Vector2Int small,
            bool premultiply, bool asNormal)
        {
            var prev = RenderTexture.active;
            RenderTexture crop = null, work = null, down = null, up = null;
            try
            {
                // 1) decode & crop / 解码并裁剪
                crop = NewRt(rect.width, rect.height);
                Decode.SetFloat("_AsNormal", asNormal ? 1f : 0f);
                var scale = new Vector2(rect.width / (float)tex.width, rect.height / (float)tex.height);
                var offset = new Vector2(rect.x / (float)tex.width, rect.y / (float)tex.height);
                BlitCrop(tex, crop, scale, offset, asNormal);

                var src = crop;
                if (premultiply)
                {
                    work = NewRt(rect.width, rect.height);
                    Graphics.Blit(crop, work, Mat, 0); // premultiply / 预乘
                    src = work;
                }

                // 2) downsample (hardware bilinear) / 硬件双线性下采样
                down = NewRt(Mathf.Max(1, small.x), Mathf.Max(1, small.y));
                src.filterMode = FilterMode.Bilinear;
                Graphics.Blit(src, down, Mat, 1);

                // 3) upsample back / 回采
                up = NewRt(rect.width, rect.height);
                down.filterMode = FilterMode.Bilinear;
                Graphics.Blit(down, up, Mat, premultiply ? 1 : 1);
                if (premultiply)
                {
                    var un = NewRt(rect.width, rect.height);
                    Graphics.Blit(up, un, Mat, 2); // unpremultiply / 反预乘
                    RenderTexture.ReleaseTemporary(up);
                    up = un;
                }

                // 4) readback / 读回
                return Readback(up, rect.width, rect.height);
            }
            finally
            {
                RenderTexture.active = prev;
                if (crop) RenderTexture.ReleaseTemporary(crop);
                if (work) RenderTexture.ReleaseTemporary(work);
                if (down) RenderTexture.ReleaseTemporary(down);
                if (up) RenderTexture.ReleaseTemporary(up);
            }
        }

        /// <summary>Plain resize of a full texture (linear, optional premultiply). / 整图缩放。</summary>
        public static NativeArray<float4> ResizeFull(Texture2D tex, Vector2Int size, bool premultiply, bool asNormal)
        {
            var prev = RenderTexture.active;
            RenderTexture full = null, work = null, down = null;
            try
            {
                full = NewRt(tex.width, tex.height);
                Decode.SetFloat("_AsNormal", asNormal ? 1f : 0f);
                Graphics.Blit(tex, full, Decode, 0);
                var src = full;
                if (premultiply)
                {
                    work = NewRt(tex.width, tex.height);
                    Graphics.Blit(full, work, Mat, 0);
                    src = work;
                }
                down = NewRt(size.x, size.y);
                Graphics.Blit(src, down, Mat, 1);
                if (premultiply)
                {
                    var un = NewRt(size.x, size.y);
                    Graphics.Blit(down, un, Mat, 2);
                    RenderTexture.ReleaseTemporary(down);
                    down = un;
                }
                return Readback(down, size.x, size.y);
            }
            finally
            {
                RenderTexture.active = prev;
                if (full) RenderTexture.ReleaseTemporary(full);
                if (work) RenderTexture.ReleaseTemporary(work);
                if (down) RenderTexture.ReleaseTemporary(down);
            }
        }

        private static void BlitCrop(Texture src, RenderTexture dst, Vector2 scale, Vector2 offset, bool asNormal)
        {
            Decode.SetFloat("_AsNormal", asNormal ? 1f : 0f);
            var prevActive = RenderTexture.active;
            Graphics.Blit(src, dst, scale, offset); // note: uses default material w/ scale-offset
            // Re-decode normals if needed: Graphics.Blit scale/offset overload can't take material,
            // so for normal maps do a second pass through the decode shader.
            // 法线需要第二遍 decode（带缩放偏移的 Blit 不支持自定义材质）。
            if (asNormal)
            {
                var tmp = NewRt(dst.width, dst.height);
                Graphics.Blit(dst, tmp, Decode, 0);
                Graphics.Blit(tmp, dst);
                RenderTexture.ReleaseTemporary(tmp);
            }
            RenderTexture.active = prevActive;
        }

        private static NativeArray<float4> Readback(RenderTexture rt, int w, int h)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var read = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            read.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            read.Apply(false);
            var raw = read.GetRawTextureData<float4>();
            var result = new NativeArray<float4>(raw.Length, Allocator.Persistent);
            result.CopyFrom(raw);
            Object.DestroyImmediate(read);
            RenderTexture.active = prev;
            return result;
        }

        public static void Cleanup()
        {
            if (_mat != null) Object.DestroyImmediate(_mat);
            if (_decode != null) Object.DestroyImmediate(_decode);
            _mat = null; _decode = null;
        }
    }
}
