// SPDX-License-Identifier: MIT
// EN: GPU backed texture reading, cropping and resampling. Works on textures that are not marked
//     readable, and never touches the user's import settings.
// ZH: 基于 GPU 的贴图读取、裁剪与重采样。可处理未勾选 Read/Write 的贴图，且绝不修改用户的导入设置。

using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Textures
{
    /// <summary>
    /// EN: A CPU side, linear space RGBA float image. Backed by a <see cref="NativeArray{T}"/> so Burst
    ///     jobs can read it without marshalling. Always dispose it.
    /// ZH: CPU 侧的线性空间 RGBA 浮点图像。底层为 <see cref="NativeArray{T}"/>，
    ///     Burst 作业可无需封送直接读取。务必释放。
    /// </summary>
    public sealed class LinearImage : IDisposable
    {
        /// <summary>EN: Width in texels. ZH: 宽度（像素）。</summary>
        public readonly int Width;
        /// <summary>EN: Height in texels. ZH: 高度（像素）。</summary>
        public readonly int Height;
        /// <summary>EN: Row major RGBA data, bottom-up like Unity's ReadPixels. ZH: 行主序 RGBA 数据，与 Unity ReadPixels 一致为自下而上。</summary>
        public NativeArray<Color> Pixels;

        /// <summary>EN: Allocates an image. ZH: 分配一张图像。</summary>
        public LinearImage(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new NativeArray<Color>(width * height, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>EN: Approximate memory footprint in bytes. ZH: 近似内存占用（字节）。</summary>
        public long ByteSize => (long)Width * Height * 16;

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Pixels.IsCreated) Pixels.Dispose();
        }
    }

    /// <summary>
    /// EN: Static helpers around <see cref="RenderTexture"/>. All colour data is kept in linear space,
    ///     which is what the quality metrics require.
    /// ZH: 围绕 <see cref="RenderTexture"/> 的静态辅助方法。所有颜色数据保持在线性空间，
    ///     这正是质量度量所要求的。
    /// </summary>
    public static class GpuTextureUtil
    {
        private static Material _premulMaterial;
        private static Material _copyMaterial;

        /// <summary>
        /// EN: Allocates a temporary linear RGBA half float render texture.
        /// ZH: 分配一张临时的线性 RGBA 半精度浮点 RenderTexture。
        /// </summary>
        public static RenderTexture GetTemp(int width, int height)
        {
            var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBHalf, 0, 1)
            {
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
                enableRandomWrite = false,
            };
            var rt = RenderTexture.GetTemporary(desc);
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            return rt;
        }

        /// <summary>EN: Releases a temporary render texture. ZH: 释放临时 RenderTexture。</summary>
        public static void Release(RenderTexture rt)
        {
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
        }

        /// <summary>
        /// EN: Copies a source texture into a linear render texture at its native resolution. Hardware
        ///     sRGB decoding means the destination always holds linear values, exactly as the target
        ///     quality algorithm demands.
        /// ZH: 将源贴图按原生分辨率拷贝进线性 RenderTexture。硬件 sRGB 解码保证目标中始终是线性值，
        ///     这正是目标质量算法所要求的。
        /// </summary>
        public static RenderTexture ToLinearRT(Texture source)
        {
            var rt = GetTemp(source.width, source.height);
            var prevActive = RenderTexture.active;
            var prevSrgbWrite = GL.sRGBWrite;
            GL.sRGBWrite = false;
            Graphics.Blit(source, rt);
            GL.sRGBWrite = prevSrgbWrite;
            RenderTexture.active = prevActive;
            return rt;
        }

        /// <summary>
        /// EN: Reads a rectangular region of a render texture back to the CPU.
        /// ZH: 将 RenderTexture 的一块矩形区域回读到 CPU。
        /// </summary>
        public static LinearImage Readback(RenderTexture rt, RectInt region)
        {
            var image = new LinearImage(region.width, region.height);
            var tmp = new Texture2D(region.width, region.height, TextureFormat.RGBAFloat, false, true);
            var prev = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                tmp.ReadPixels(new Rect(region.x, region.y, region.width, region.height), 0, 0, false);
                tmp.Apply(false, false);
                var raw = tmp.GetRawTextureData<Color>();
                image.Pixels.CopyFrom(raw);
            }
            finally
            {
                RenderTexture.active = prev;
                UnityEngine.Object.DestroyImmediate(tmp);
            }
            return image;
        }

        /// <summary>
        /// EN: Downsamples a region of <paramref name="source"/> into a new render texture of the given
        ///     size. Alpha is premultiplied before filtering when <paramref name="premultiplyAlpha"/> is
        ///     set, which is required so that fully transparent texels do not bleed their colour in.
        /// ZH: 将 <paramref name="source"/> 的一块区域降采样到给定尺寸的新 RenderTexture。
        ///     当设置 <paramref name="premultiplyAlpha"/> 时会在滤波前预乘 alpha，
        ///     这是为了避免全透明像素把颜色渗进来。
        /// </summary>
        public static RenderTexture Downsample(RenderTexture source, RectInt region, Vector2Int targetSize, bool premultiplyAlpha)
        {
            EnsureMaterials();

            // EN: Crop first so the box filter never reaches outside the island.
            // ZH: 先裁剪，使盒式滤波永远不会越过岛的边界。
            var cropped = GetTemp(region.width, region.height);
            var scale = new Vector2((float)region.width / source.width, (float)region.height / source.height);
            var offset = new Vector2((float)region.x / source.width, (float)region.y / source.height);

            var prevSrgb = GL.sRGBWrite;
            GL.sRGBWrite = false;
            if (premultiplyAlpha)
            {
                _premulMaterial.SetVector("_ATO_ScaleOffset", new Vector4(scale.x, scale.y, offset.x, offset.y));
                _premulMaterial.SetFloat("_ATO_Mode", 0f); // premultiply
                Graphics.Blit(source, cropped, _premulMaterial);
            }
            else
            {
                Graphics.Blit(source, cropped, scale, offset);
            }

            // EN: Successive halving gives a proper box pyramid instead of one aliased bilinear tap.
            // ZH: 逐级折半可得到正确的盒式金字塔，而不是一次会走样的双线性采样。
            var current = cropped;
            while (current.width > targetSize.x * 2 || current.height > targetSize.y * 2)
            {
                int nw = Mathf.Max(targetSize.x, current.width / 2);
                int nh = Mathf.Max(targetSize.y, current.height / 2);
                var next = GetTemp(nw, nh);
                Graphics.Blit(current, next);
                Release(current);
                current = next;
            }

            RenderTexture result;
            if (current.width != targetSize.x || current.height != targetSize.y)
            {
                result = GetTemp(targetSize.x, targetSize.y);
                Graphics.Blit(current, result);
                Release(current);
            }
            else
            {
                result = current;
            }

            if (premultiplyAlpha)
            {
                var unpremul = GetTemp(targetSize.x, targetSize.y);
                _premulMaterial.SetVector("_ATO_ScaleOffset", new Vector4(1, 1, 0, 0));
                _premulMaterial.SetFloat("_ATO_Mode", 1f); // unpremultiply
                Graphics.Blit(result, unpremul, _premulMaterial);
                Release(result);
                result = unpremul;
            }

            GL.sRGBWrite = prevSrgb;
            return result;
        }

        /// <summary>
        /// EN: Upsamples with bilinear filtering, used to compare a downscaled island against the original.
        /// ZH: 使用双线性滤波上采样，用于将缩小后的岛与原图进行比较。
        /// </summary>
        public static RenderTexture BilinearUpsample(RenderTexture source, Vector2Int targetSize)
        {
            var rt = GetTemp(targetSize.x, targetSize.y);
            var prevFilter = source.filterMode;
            source.filterMode = FilterMode.Bilinear;
            var prevSrgb = GL.sRGBWrite;
            GL.sRGBWrite = false;
            Graphics.Blit(source, rt);
            GL.sRGBWrite = prevSrgb;
            source.filterMode = prevFilter;
            return rt;
        }

        private static void EnsureMaterials()
        {
            if (_premulMaterial == null)
            {
                var shader = Shader.Find("Hidden/ATO/PremultiplyAlpha");
                if (shader == null)
                    throw new InvalidOperationException("[ATO] Hidden/ATO/PremultiplyAlpha shader is missing from the package.");
                _premulMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_copyMaterial == null)
            {
                var shader = Shader.Find("Hidden/ATO/Copy");
                if (shader != null)
                    _copyMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
        }

        /// <summary>
        /// EN: Encodes a linear render texture into a <see cref="Texture2D"/> asset.
        /// ZH: 将线性 RenderTexture 编码为 <see cref="Texture2D"/> 资产。
        /// </summary>
        /// <param name="rt">EN: Source. ZH: 源。</param>
        /// <param name="sRgb">EN: Store as sRGB. ZH: 以 sRGB 存储。</param>
        /// <param name="mipmaps">EN: Generate mipmaps. ZH: 生成 Mipmap。</param>
        public static Texture2D ToTexture2D(RenderTexture rt, bool sRgb, bool mipmaps)
        {
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, mipmaps, !sRgb);
            var prev = RenderTexture.active;
            try
            {
                // EN: When writing an sRGB asset we must let the GPU encode on the way out.
                // ZH: 写出 sRGB 资产时必须让 GPU 在输出时完成编码。
                var encoded = RenderTexture.GetTemporary(new RenderTextureDescriptor(rt.width, rt.height, RenderTextureFormat.ARGB32, 0, 1)
                {
                    sRGB = sRgb,
                });
                var prevSrgbWrite = GL.sRGBWrite;
                GL.sRGBWrite = sRgb;
                Graphics.Blit(rt, encoded);
                GL.sRGBWrite = prevSrgbWrite;

                RenderTexture.active = encoded;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                tex.Apply(mipmaps, false);
                RenderTexture.ReleaseTemporary(encoded);
            }
            finally
            {
                RenderTexture.active = prev;
            }
            return tex;
        }
    }
}
