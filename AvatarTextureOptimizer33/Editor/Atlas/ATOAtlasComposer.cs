// SPDX-License-Identifier: MIT
// EN: Atlas composition: island resampling into the atlas buffer plus pull-push edge bleeding
//     (GPU compute when available, deterministic CPU fallback otherwise).
// ZH: 图集合成：把岛重采样写入图集缓冲，并做 pull-push 边缘外扩
//     （可用时走 GPU compute，否则使用确定性的 CPU 回退实现）。

using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: CPU side working buffer of one atlas. Colours are linear, coverage marks written texels.
    /// ZH: 单张图集的 CPU 工作缓冲。颜色为线性，coverage 标记已写入的纹素。
    /// </summary>
    public sealed class ATOAtlasBuffer : IDisposable
    {
        public int Width;
        public int Height;
        public NativeArray<half4> Pixels;
        public NativeArray<byte> Coverage;
        public long CoveredTexels;

        public static ATOAtlasBuffer Create(int width, int height)
        {
            return new ATOAtlasBuffer
            {
                Width = width,
                Height = height,
                Pixels = new NativeArray<half4>(width * height, Allocator.Persistent,
                    NativeArrayOptions.ClearMemory),
                Coverage = new NativeArray<byte>(width * height, Allocator.Persistent,
                    NativeArrayOptions.ClearMemory),
            };
        }

        public void Dispose()
        {
            if (Pixels.IsCreated) Pixels.Dispose();
            if (Coverage.IsCreated) Coverage.Dispose();
        }
    }

    /// <summary>
    /// EN: Composes atlases.
    /// ZH: 图集合成器。
    /// </summary>
    public sealed class ATOAtlasComposer : IDisposable
    {
        private readonly ATOLog _log;
        private readonly ATOTextureCache _cache;
        private ComputeShader _pullPush;
        private bool _pullPushLoaded;

        public ATOAtlasComposer(ATOLog log, ATOTextureCache cache)
        {
            _log = log;
            _cache = cache;
        }

        /// <summary>
        /// EN: Resamples one island of one texture into the atlas buffer.
        /// ZH: 把某贴图的某个岛重采样写入图集缓冲。
        /// </summary>
        public void BlitIsland(ATOAtlasBuffer buffer, ATOTextureInfo texture, ATOIsland island, float classScale)
        {
            var placement = island.Placement;
            if (!placement.Valid) return;

            var decoded = _cache.Get(texture.Source, texture.Role == ATOTextureRole.Normal);
            var rect = ATORaster.IslandPixelRect(island.Bounds, decoded.Width, decoded.Height);

            var dstW = Mathf.Max(1, Mathf.RoundToInt(placement.Width * classScale));
            var dstH = Mathf.Max(1, Mathf.RoundToInt(placement.Height * classScale));
            var dstX = Mathf.RoundToInt(placement.X * classScale);
            var dstY = Mathf.RoundToInt(placement.Y * classScale);

            // EN: When rotated, the source block is resampled transposed. ZH: 旋转时源块以转置方式重采样。
            var sampleW = placement.Rotated ? dstH : dstW;
            var sampleH = placement.Rotated ? dstW : dstH;

            var premultiply = texture.Role == ATOTextureRole.ColorTransparent;
            var region = new NativeArray<float4>(rect.width * rect.height, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);
            var small = new NativeArray<float4>(sampleW * sampleH, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);

            try
            {
                new ATOExtractRegionJob
                {
                    Source = decoded.Pixels,
                    Destination = region,
                    SourceWidth = decoded.Width,
                    SourceHeight = decoded.Height,
                    X0 = rect.x,
                    Y0 = rect.y,
                    Width = rect.width,
                    Height = rect.height,
                    PremultiplyAlpha = premultiply,
                }.Schedule(rect.height, 1).Complete();

                new ATODownsampleJob
                {
                    Source = region,
                    Destination = small,
                    SrcWidth = rect.width,
                    SrcHeight = rect.height,
                    DstWidth = sampleW,
                    DstHeight = sampleH,
                }.Schedule(sampleH, 1).Complete();

                for (var y = 0; y < sampleH; y++)
                for (var x = 0; x < sampleW; x++)
                {
                    var c = small[y * sampleW + x];

                    if (premultiply) c = new float4(c.w > 1e-5f ? c.xyz / c.w : float3.zero, c.w);
                    if (texture.Role == ATOTextureRole.Normal)
                        c = new float4(math.normalizesafe(c.xyz, new float3(0, 0, 1)), 1f);

                    // EN: 90 degree rotation of the block; tangents are never recomputed.
                    // ZH: 对块做 90 度旋转；绝不重算切线。
                    var tx = placement.Rotated ? dstW - 1 - y : x;
                    var ty = placement.Rotated ? x : y;

                    var px = dstX + tx;
                    var py = dstY + ty;
                    if (px < 0 || py < 0 || px >= buffer.Width || py >= buffer.Height) continue;

                    var idx = py * buffer.Width + px;
                    buffer.Pixels[idx] = new half4((half)c.x, (half)c.y, (half)c.z, (half)c.w);
                    if (buffer.Coverage[idx] == 0)
                    {
                        buffer.Coverage[idx] = 1;
                        buffer.CoveredTexels++;
                    }
                }
            }
            finally
            {
                region.Dispose();
                small.Dispose();
            }
        }

        /// <summary>
        /// EN: Fills every empty texel with the nearest island colour using a pull-push pyramid.
        ///     Alpha is preserved: transparent atlases keep alpha 0 outside the islands.
        /// ZH: 用 pull-push 金字塔把所有空白纹素填充为最近的岛颜色。
        ///     alpha 保持不变：透明图集在岛外的 alpha 依然是 0。
        /// </summary>
        public void PullPushFill(ATOAtlasBuffer buffer)
        {
            if (buffer.CoveredTexels == 0) return;

            if (TryPullPushGPU(buffer)) return;
            PullPushCPU(buffer);
        }

        // ------------------------------------------------------------------ GPU path

        private ComputeShader LoadPullPush()
        {
            if (_pullPushLoaded) return _pullPush;
            _pullPushLoaded = true;
            var path = ATOPackagePaths.ShaderDirectory + "/ATOPullPush.compute";
            _pullPush = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            if (_pullPush == null) _log.Warning("atlas", $"compute shader not found at '{path}', using CPU fallback");
            return _pullPush;
        }

        private bool TryPullPushGPU(ATOAtlasBuffer buffer)
        {
            if (!SystemInfo.supportsComputeShaders) return false;

            var shader = LoadPullPush();
            if (shader == null) return false;

            RenderTexture[] pyramid = null;
            Texture2D upload = null;

            try
            {
                var levels = 1;
                while ((buffer.Width >> levels) > 0 && (buffer.Height >> levels) > 0) levels++;

                pyramid = new RenderTexture[levels];
                for (var i = 0; i < levels; i++)
                {
                    var w = Mathf.Max(1, buffer.Width >> i);
                    var h = Mathf.Max(1, buffer.Height >> i);
                    pyramid[i] = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                    {
                        enableRandomWrite = true,
                        useMipMap = false,
                        autoGenerateMips = false,
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                    pyramid[i].Create();
                }

                // EN: Level 0 = colour with validity in alpha. ZH: 第 0 层 = 颜色，alpha 存有效性。
                upload = new Texture2D(buffer.Width, buffer.Height, TextureFormat.RGBAHalf, false, true)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                var raw = upload.GetRawTextureData<half4>();
                for (var i = 0; i < raw.Length; i++)
                {
                    var c = buffer.Pixels[i];
                    var valid = buffer.Coverage[i] != 0 ? (half)1f : (half)0f;
                    raw[i] = new half4(c.x, c.y, c.z, valid);
                }

                upload.Apply(false, false);
                Graphics.Blit(upload, pyramid[0]);

                var pullKernel = shader.FindKernel("ATOPullDown");
                var pushKernel = shader.FindKernel("ATOPushUp");

                for (var i = 1; i < pyramid.Length; i++)
                {
                    shader.SetTexture(pullKernel, "_PyramidSrc", pyramid[i - 1]);
                    shader.SetTexture(pullKernel, "_PyramidDst", pyramid[i]);
                    shader.SetInts("_LevelSizeSrc", pyramid[i - 1].width, pyramid[i - 1].height, 0, 0);
                    shader.SetInts("_LevelSizeDst", pyramid[i].width, pyramid[i].height, 0, 0);
                    shader.Dispatch(pullKernel, Mathf.CeilToInt(pyramid[i].width / 8f),
                        Mathf.CeilToInt(pyramid[i].height / 8f), 1);
                }

                for (var i = pyramid.Length - 2; i >= 0; i--)
                {
                    shader.SetTexture(pushKernel, "_PyramidSrc", pyramid[i + 1]);
                    shader.SetTexture(pushKernel, "_PyramidDst", pyramid[i]);
                    shader.SetInts("_LevelSizeSrc", pyramid[i + 1].width, pyramid[i + 1].height, 0, 0);
                    shader.SetInts("_LevelSizeDst", pyramid[i].width, pyramid[i].height, 0, 0);
                    shader.Dispatch(pushKernel, Mathf.CeilToInt(pyramid[i].width / 8f),
                        Mathf.CeilToInt(pyramid[i].height / 8f), 1);
                }

                // EN: Read back and merge, keeping the original alpha of covered texels.
                // ZH: 回读并合并，已覆盖纹素保留原有 alpha。
                var readback = new Texture2D(buffer.Width, buffer.Height, TextureFormat.RGBAHalf, false, true)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                var prev = RenderTexture.active;
                try
                {
                    RenderTexture.active = pyramid[0];
                    readback.ReadPixels(new Rect(0, 0, buffer.Width, buffer.Height), 0, 0, false);
                    readback.Apply(false, false);
                }
                finally
                {
                    RenderTexture.active = prev;
                }

                var filled = readback.GetRawTextureData<half4>();
                for (var i = 0; i < filled.Length; i++)
                {
                    if (buffer.Coverage[i] != 0) continue;
                    var c = filled[i];
                    var original = buffer.Pixels[i];
                    buffer.Pixels[i] = new half4(c.x, c.y, c.z, original.w);
                }

                UnityEngine.Object.DestroyImmediate(readback);
                _log.Trace("atlas", $"pull-push (GPU) done on {buffer.Width}x{buffer.Height}, {levels} levels");
                return true;
            }
            catch (Exception e)
            {
                _log.Warning("atlas", $"GPU pull-push failed ({e.Message}), falling back to CPU");
                return false;
            }
            finally
            {
                if (upload != null) UnityEngine.Object.DestroyImmediate(upload);
                if (pyramid != null)
                {
                    foreach (var rt in pyramid)
                    {
                        if (rt == null) continue;
                        rt.Release();
                        UnityEngine.Object.DestroyImmediate(rt);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ CPU path

        private void PullPushCPU(ATOAtlasBuffer buffer)
        {
            var levels = 1;
            while ((buffer.Width >> levels) > 0 && (buffer.Height >> levels) > 0) levels++;

            var sizes = new Vector2Int[levels];
            var data = new NativeArray<float4>[levels];

            try
            {
                for (var i = 0; i < levels; i++)
                {
                    sizes[i] = new Vector2Int(Mathf.Max(1, buffer.Width >> i), Mathf.Max(1, buffer.Height >> i));
                    data[i] = new NativeArray<float4>(sizes[i].x * sizes[i].y, Allocator.Persistent,
                        NativeArrayOptions.ClearMemory);
                }

                for (var i = 0; i < data[0].Length; i++)
                {
                    var c = (float4)buffer.Pixels[i];
                    data[0][i] = new float4(c.xyz, buffer.Coverage[i] != 0 ? 1f : 0f);
                }

                for (var l = 1; l < levels; l++)
                {
                    var src = data[l - 1];
                    var dst = data[l];
                    var sw = sizes[l - 1].x;
                    var sh = sizes[l - 1].y;
                    var dw = sizes[l].x;
                    var dh = sizes[l].y;

                    for (var y = 0; y < dh; y++)
                    for (var x = 0; x < dw; x++)
                    {
                        var sum = float3.zero;
                        var weight = 0f;
                        for (var dy = 0; dy < 2; dy++)
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var sx = x * 2 + dx;
                            var sy = y * 2 + dy;
                            if (sx >= sw || sy >= sh) continue;
                            var c = src[sy * sw + sx];
                            sum += c.xyz * c.w;
                            weight += c.w;
                        }

                        dst[y * dw + x] = weight > 0f ? new float4(sum / weight, 1f) : float4.zero;
                    }
                }

                for (var l = levels - 2; l >= 0; l--)
                {
                    var coarse = data[l + 1];
                    var fine = data[l];
                    var cw = sizes[l + 1].x;
                    var ch = sizes[l + 1].y;
                    var fw = sizes[l].x;
                    var fh = sizes[l].y;

                    for (var y = 0; y < fh; y++)
                    for (var x = 0; x < fw; x++)
                    {
                        var idx = y * fw + x;
                        if (fine[idx].w > 0.5f) continue;
                        var cx = Mathf.Min(cw - 1, x / 2);
                        var cy = Mathf.Min(ch - 1, y / 2);
                        var c = coarse[cy * cw + cx];
                        if (c.w > 0f) fine[idx] = new float4(c.xyz, 1f);
                    }
                }

                var final = data[0];
                for (var i = 0; i < final.Length; i++)
                {
                    if (buffer.Coverage[i] != 0) continue;
                    var c = final[i];
                    var original = buffer.Pixels[i];
                    buffer.Pixels[i] = new half4((half)c.x, (half)c.y, (half)c.z, original.w);
                }

                _log.Trace("atlas", $"pull-push (CPU) done on {buffer.Width}x{buffer.Height}, {levels} levels");
            }
            finally
            {
                foreach (var d in data)
                    if (d.IsCreated)
                        d.Dispose();
            }
        }

        public void Dispose()
        {
        }
    }
}
