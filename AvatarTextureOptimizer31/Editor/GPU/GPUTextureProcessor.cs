// GPUTextureProcessor.cs
// GPU-accelerated texture processing using RenderTexture for batch operations.
// Handles bilinear upscaling/downscaling, quality evaluation, and pull-push bleeding.
// 使用 RenderTexture 的 GPU 加速贴图处理。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.GPU
{
    /// <summary>
    /// Provides GPU-accelerated texture operations via RenderTexture.
    /// Falls back to CPU processing when GPU is unavailable.
    /// 提供 GPU 加速的贴图操作，GPU 不可用时回退到 CPU。
    /// </summary>
    internal static class GPUTextureProcessor
    {
        internal static bool IsGPUAvailable => SystemInfo.supportsComputeShaders || SystemInfo.supportsRenderTextures;

        /// <summary>
        /// Bilinearly resizes a texture using GPU RenderTexture blit.
        /// 使用 GPU RenderTexture blit 双线性缩放贴图。
        /// </summary>
        internal static Texture2D Resize(Texture2D source, int targetW, int targetH, bool mipChain = true)
        {
            if (source == null) return null;
            if (!IsGPUAvailable)
            {
                return ResizeCPU(source, targetW, targetH);
            }

            var format = source.format;
            if (!GraphicsFormatUtility.IsCompressedFormat(GraphicsFormatUtility.GetGraphicsFormat(format, true)))
            {
                format = TextureFormat.RGBA32;
            }

            var rt = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            var result = new Texture2D(targetW, targetH, TextureFormat.RGBA32, mipChain);

            var prevRT = RenderTexture.active;
            RenderTexture.active = rt;

            try
            {
                // Ensure source is readable
                Texture2D readableSource = source;
                if (!source.isReadable)
                {
                    readableSource = GetReadableCopy(source);
                }

                Graphics.Blit(readableSource, rt);
                result.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
                result.Apply(true);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ATO] GPU resize failed: {ex.Message}");
                return ResizeCPU(source, targetW, targetH);
            }
            finally
            {
                RenderTexture.active = prevRT;
                RenderTexture.ReleaseTemporary(rt);
            }

            return result;
        }

        /// <summary>
        /// Bilinearly resizes a texture region using GPU.
        /// 使用 GPU 双线性缩放贴图区域。
        /// </summary>
        internal static Texture2D ResizeRegion(Texture2D source, Rect region, int targetW, int targetH)
        {
            if (source == null || !source.isReadable) return null;

            int srcX = Mathf.RoundToInt(region.x);
            int srcY = Mathf.RoundToInt(region.y);
            int srcW = Mathf.RoundToInt(region.width);
            int srcH = Mathf.RoundToInt(region.height);

            // Extract region
            var regionPixels = source.GetPixels(srcX, srcY, srcW, srcH);
            var regionTex = new Texture2D(srcW, srcH, TextureFormat.RGBA32, false);
            regionTex.SetPixels(regionPixels);
            regionTex.Apply();

            return Resize(regionTex, targetW, targetH, false);
        }

        private static Texture2D GetReadableCopy(Texture2D source)
        {
            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            Graphics.Blit(source, rt);
            var readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return readable;
        }

        private static Texture2D ResizeCPU(Texture2D source, int targetW, int targetH)
        {
            if (!source.isReadable) return GetReadableCopy(source);
            var result = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false);
            var pixels = source.GetPixels32();

            for (int y = 0; y < targetH; y++)
            {
                for (int x = 0; x < targetW; x++)
                {
                    float u = (float)x / Mathf.Max(1, targetW - 1) * (source.width - 1);
                    float v = (float)y / Mathf.Max(1, targetH - 1) * (source.height - 1);
                    int sx = Mathf.Clamp(Mathf.RoundToInt(u), 0, source.width - 1);
                    int sy = Mathf.Clamp(Mathf.RoundToInt(v), 0, source.height - 1);
                    result.SetPixel(x, y, pixels[sy * source.width + sx]);
                }
            }
            result.Apply();
            return result;
        }
    }
}
