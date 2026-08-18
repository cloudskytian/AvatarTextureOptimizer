// Copyright (c) fosa. Licensed under the MIT License.
// Blits islands into their packed atlas positions and dilates the padding via GPU pull-push.
// 将岛块 blit 到其装箱位置，并通过 GPU pull-push 外扩填充间距区域。

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Composites one atlas texture per source texture in a UV group.
    /// 为 UV 组中的每张源贴图合成一张图集贴图。
    /// </summary>
    public sealed class AtlasCompositor : IDisposable
    {
        private const string ShaderName = "Hidden/ATO/PullPush";

        /// <summary>Shader pass indices, matching the order declared in ATOPullPush.shader. / 着色器 pass 索引，与 ATOPullPush.shader 中的声明顺序一致。</summary>
        private const int PassPull = 0;

        private const int PassPush = 1;
        private const int PassResolve = 2;

        private static readonly int CoarseTexId = Shader.PropertyToID("_CoarseTex");
        private static readonly int OriginalTexId = Shader.PropertyToID("_OriginalTex");
        private static readonly int CoverageTexId = Shader.PropertyToID("_CoverageTex");

        private readonly ATOLogger _log;
        private readonly TextureCache _cache;
        private Material _pullPush;

        /// <summary>Creates a compositor. / 创建合成器。</summary>
        public AtlasCompositor(ATOLogger log, TextureCache cache)
        {
            _log = log;
            _cache = cache;

            var shader = Shader.Find(ShaderName);
            if (shader != null)
            {
                _pullPush = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
        }

        /// <summary>
        /// Renders every island of <paramref name="source" /> into a new atlas texture.
        /// 将 <paramref name="source" /> 的所有岛渲染到一张新的图集贴图中。
        /// </summary>
        /// <param name="source">Source texture supplying the pixels. / 提供像素的源贴图。</param>
        /// <param name="islands">Islands with packed positions resolved. / 已确定装箱位置的岛。</param>
        /// <param name="atlasWidth">Atlas width in pixels. / 图集宽度（像素）。</param>
        /// <param name="atlasHeight">Atlas height in pixels. / 图集高度（像素）。</param>
        /// <param name="isSRGB">Whether the atlas stores sRGB data. / 图集是否存储 sRGB 数据。</param>
        /// <param name="isNormalMap">Normals need vector-space filtering. / 法线需要向量空间过滤。</param>
        /// <param name="padding">Dilation distance in pixels. / 外扩距离（像素）。</param>
        public Texture2D Composite(
            Texture2D source,
            IReadOnlyList<UVIsland> islands,
            int atlasWidth,
            int atlasHeight,
            bool isSRGB,
            bool isNormalMap,
            int padding)
        {
            if (source == null || islands == null || islands.Count == 0) return null;

            var decoded = _cache.Get(source);
            if (decoded == null)
            {
                _log?.Warning($"Could not decode {source.name}; skipping atlas composition");
                return null;
            }

            // Compose on the CPU in linear float space, which keeps the resampling maths
            // identical to what the quality search evaluated.
            // 在 CPU 上以线性浮点空间合成，
            // 使重采样数学与质量搜索所评估的完全一致。
            var atlas = new ImageBuffer(atlasWidth, atlasHeight);
            var coverage = new float[atlasWidth * atlasHeight];

            foreach (var island in islands)
            {
                if (island.AtlasIndex < 0) continue;
                BlitIsland(decoded, atlas, coverage, island, isNormalMap);
            }

            // Prefer the GPU pull-push path: it propagates colour across the whole atlas in
            // O(log n) passes instead of one ring per iteration, which matters for 8k atlases.
            // Falls back to the CPU implementation when the shader is unavailable.
            // 优先使用 GPU pull-push 路径：它以 O(log n) 个 pass 在整张图集上传播颜色，
            // 而非每次迭代仅外扩一圈，这对 8k 图集意义重大。
            // 着色器不可用时回退到 CPU 实现。
            if (!DilateGpu(atlas, coverage, atlasWidth, atlasHeight, isNormalMap))
            {
                Dilate(atlas, coverage, atlasWidth, atlasHeight, padding, isNormalMap);
            }

            var result = new Texture2D(
                atlasWidth, atlasHeight, TextureFormat.RGBA32, true, !isSRGB)
            {
                name = "ATO_Atlas",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = source.filterMode,
                anisoLevel = source.anisoLevel,
            };

            result.SetPixels(atlas.Pixels);
            result.Apply(true, false);
            return result;
        }

        /// <summary>
        /// Copies one island's pixels into its packed slot, applying scale and 90 degree rotation.
        /// 将单个岛的像素复制到其装箱位置，并应用缩放与 90 度旋转。
        /// </summary>
        private void BlitIsland(
            DecodedTexture src,
            ImageBuffer dst,
            float[] coverage,
            UVIsland island,
            bool isNormalMap)
        {
            // Derive the source rect from UV bounds against THIS texture's dimensions. The rect
            // cached on the island belongs to whichever texture was measured last during the
            // quality search, and textures in one UV group may differ in resolution, so reusing
            // it would sample the wrong region.
            // 依据 UV 包围盒与**当前这张**贴图的尺寸推导源矩形。
            // 岛上缓存的矩形属于质量搜索中最后测量的那张贴图，
            // 而同一 UV 组内各贴图分辨率可能不同，直接复用会采样到错误区域。
            var srcRect = OptimizationPipeline.ComputeSourceRect(island, src.Width, src.Height);
            if (srcRect.width <= 0 || srcRect.height <= 0) return;

            // Extract, resample to the packed size, then place.
            // 提取 → 重采样到装箱尺寸 → 放置。
            var cropped = Resampler.Crop(src, srcRect);

            // Always resample to the unrotated PackedSize. Resampling straight into the swapped
            // dimensions would squash the island's aspect ratio rather than rotate it; the
            // rotation is applied afterwards as an exact index transpose during placement.
            // 始终重采样到未旋转的 PackedSize。
            // 直接重采样到交换后的尺寸会压扁岛的长宽比而非旋转它；
            // 旋转在放置时以精确的索引转置施加。
            var targetW = island.PackedSize.x;
            var targetH = island.PackedSize.y;
            if (targetW <= 0 || targetH <= 0) return;

            ImageBuffer scaled;
            if (cropped.Width == targetW && cropped.Height == targetH)
            {
                scaled = cropped;
            }
            else if (isNormalMap)
            {
                scaled = Resampler.ResampleNormalMap(cropped, targetW, targetH);
            }
            else
            {
                scaled = Resampler.Downsample(cropped, targetW, targetH, true);
            }

            var pos = island.PackedPosition;

            for (var y = 0; y < scaled.Height; y++)
            {
                for (var x = 0; x < scaled.Width; x++)
                {
                    // A 90 degree rotation is a pure index transpose, so it is exact: no
                    // resampling error and no need to recompute tangents.
                    // 90 度旋转是纯索引转置，因此是精确的：
                    // 既无重采样误差，也无需重算切线。
                    int dx, dy;
                    if (island.Rotated)
                    {
                        dx = pos.x + (scaled.Height - 1 - y);
                        dy = pos.y + x;
                    }
                    else
                    {
                        dx = pos.x + x;
                        dy = pos.y + y;
                    }

                    if (dx < 0 || dy < 0 || dx >= dst.Width || dy >= dst.Height) continue;

                    var di = dy * dst.Width + dx;
                    dst.Pixels[di] = scaled.Pixels[y * scaled.Width + x];
                    coverage[di] = 1f;
                }
            }
        }

        /// <summary>
        /// GPU pull-push dilation. The pull phase builds a coverage-weighted mip pyramid; the
        /// push phase fills unwritten texels from the next coarser level. Because each level
        /// halves the resolution, colour reaches arbitrarily distant texels in log2(size) passes,
        /// which is what makes the dilation effectively infinite.
        /// GPU pull-push 外扩。pull 阶段构建按覆盖度加权的 mip 金字塔；
        /// push 阶段用更粗一级填充未写入的 texel。
        /// 由于每级分辨率减半，颜色可在 log2(尺寸) 个 pass 内到达任意远的 texel，
        /// 这正是「无限外扩」的实现方式。
        /// </summary>
        /// <returns>False when the GPU path is unavailable. / GPU 路径不可用时返回 false。</returns>
        private bool DilateGpu(
            ImageBuffer buffer, float[] coverage, int width, int height, bool isNormalMap)
        {
            if (_pullPush == null) return false;

            RenderTexture[] pyramid = null;
            RenderTexture source = null;
            RenderTexture resolved = null;
            Texture2D packed = null;
            Texture2D original = null;
            Texture2D coverageTex = null;

            var previousActive = RenderTexture.active;

            try
            {
                // Pack colour with coverage in alpha. Coverage must be carried separately from
                // image alpha, otherwise a transparent texel inside an island reads as unwritten.
                // 将颜色与覆盖度（存于 alpha）打包。
                // 覆盖度必须与图像 alpha 分开携带，否则岛内部的透明 texel 会被读作未写入。
                packed = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
                original = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
                coverageTex = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);

                var packedPixels = new Color[buffer.Pixels.Length];
                var coveragePixels = new Color[buffer.Pixels.Length];

                for (var i = 0; i < buffer.Pixels.Length; i++)
                {
                    var c = buffer.Pixels[i];
                    var w = coverage[i] > 0f ? 1f : 0f;
                    packedPixels[i] = new Color(c.r, c.g, c.b, w);
                    coveragePixels[i] = new Color(w, w, w, w);
                }

                packed.SetPixels(packedPixels);
                packed.Apply(false, false);
                original.SetPixels(buffer.Pixels);
                original.Apply(false, false);
                coverageTex.SetPixels(coveragePixels);
                coverageTex.Apply(false, false);

                source = NewTarget(width, height);
                Graphics.Blit(packed, source);

                // ---- Pull: build the pyramid down to 1x1. ----
                // ---- Pull：向下构建金字塔直到 1x1。----
                var levels = 1;
                for (var s = Mathf.Max(width, height); s > 1; s >>= 1) levels++;

                pyramid = new RenderTexture[levels];
                pyramid[0] = source;

                for (var i = 1; i < levels; i++)
                {
                    var w = Mathf.Max(1, width >> i);
                    var h = Mathf.Max(1, height >> i);
                    pyramid[i] = NewTarget(w, h);
                    Graphics.Blit(pyramid[i - 1], pyramid[i], _pullPush, PassPull);
                }

                // ---- Push: fill coarse-to-fine. ----
                // ---- Push：由粗到细回填。----
                for (var i = levels - 2; i >= 0; i--)
                {
                    var target = NewTarget(pyramid[i].width, pyramid[i].height);
                    _pullPush.SetTexture(CoarseTexId, pyramid[i + 1]);
                    Graphics.Blit(pyramid[i], target, _pullPush, PassPush);

                    ReleaseTarget(pyramid[i]);
                    pyramid[i] = target;
                }

                // ---- Resolve: restore authored texels, force padding alpha to zero. ----
                // ---- Resolve：恢复原有 texel，并将填充区 alpha 强制为 0。----
                resolved = NewTarget(width, height);
                _pullPush.SetTexture(OriginalTexId, original);
                _pullPush.SetTexture(CoverageTexId, coverageTex);
                Graphics.Blit(pyramid[0], resolved, _pullPush, PassResolve);

                RenderTexture.active = resolved;
                var readback = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                readback.Apply(false, false);

                var output = readback.GetPixels();
                if (output == null || output.Length != buffer.Pixels.Length)
                {
                    UnityEngine.Object.DestroyImmediate(readback);
                    return false;
                }

                if (isNormalMap) RenormalizeUncovered(output, coverage);

                Array.Copy(output, buffer.Pixels, output.Length);
                UnityEngine.Object.DestroyImmediate(readback);
                return true;
            }
            catch (Exception e)
            {
                _log?.Warning(
                    $"GPU dilation failed ({e.Message}); falling back to the CPU path");
                return false;
            }
            finally
            {
                RenderTexture.active = previousActive;

                if (pyramid != null)
                {
                    foreach (var rt in pyramid) ReleaseTarget(rt);
                }
                else
                {
                    ReleaseTarget(source);
                }

                ReleaseTarget(resolved);
                DestroyTemp(packed);
                DestroyTemp(original);
                DestroyTemp(coverageTex);
            }
        }

        /// <summary>
        /// Re-normalises dilated normals, which averaging in encoded space denormalises.
        /// 重新归一化外扩后的法线，因为在编码空间求平均会破坏其单位长度。
        /// </summary>
        private static void RenormalizeUncovered(Color[] pixels, float[] coverage)
        {
            for (var i = 0; i < pixels.Length; i++)
            {
                if (coverage[i] > 0f) continue;

                var c = pixels[i];
                var v = new Vector3(c.r * 2f - 1f, c.g * 2f - 1f, c.b * 2f - 1f);
                if (v.sqrMagnitude <= 1e-8f) continue;

                v = v.normalized;
                pixels[i] = new Color(
                    v.x * 0.5f + 0.5f, v.y * 0.5f + 0.5f, v.z * 0.5f + 0.5f, c.a);
            }
        }

        private static RenderTexture NewTarget(int width, int height)
        {
            // Linear float targets: the whole pipeline works in linear space, and half precision
            // would visibly band across a large dilated gradient.
            // 线性浮点目标：整条管线都在线性空间工作，
            // 半精度会在大范围外扩的渐变上产生可见色带。
            var rt = new RenderTexture(
                width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            rt.Create();
            return rt;
        }

        private static void ReleaseTarget(RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
        }

        private static void DestroyTemp(Texture2D tex)
        {
            if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
        }

        /// <summary>
        /// Pull-push dilation performed on the CPU over the linear buffer. Colour is spread into
        /// uncovered texels while their alpha is forced to zero, so padding is never visible but
        /// bilinear filtering at island edges never picks up background.
        /// 在 CPU 上对线性缓冲执行 pull-push 外扩。
        /// 颜色被扩散到未覆盖的 texel，同时其 alpha 强制为 0，
        /// 使填充永不可见，而岛边缘的双线性过滤也不会采样到背景。
        /// </summary>
        private static void Dilate(
            ImageBuffer buffer,
            float[] coverage,
            int width,
            int height,
            int padding,
            bool isNormalMap)
        {
            if (padding <= 0) return;

            var current = (float[])coverage.Clone();
            var next = new float[coverage.Length];
            var colors = buffer.Pixels;
            var pending = new Color[colors.Length];

            // Iterate one ring at a time. Padding is bounded, so this terminates quickly and
            // still behaves like infinite dilation within the region that matters.
            // 每次外扩一圈。padding 有上界，因此收敛很快，
            // 且在真正重要的区域内其行为等同于无限外扩。
            for (var iteration = 0; iteration < padding; iteration++)
            {
                Array.Copy(current, next, current.Length);
                var changed = false;

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var i = y * width + x;
                        if (current[i] > 0f) continue;

                        float r = 0, g = 0, b = 0, a = 0, w = 0;

                        for (var oy = -1; oy <= 1; oy++)
                        {
                            var ny = y + oy;
                            if (ny < 0 || ny >= height) continue;

                            for (var ox = -1; ox <= 1; ox++)
                            {
                                if (ox == 0 && oy == 0) continue;
                                var nx = x + ox;
                                if (nx < 0 || nx >= width) continue;

                                var ni = ny * width + nx;
                                if (current[ni] <= 0f) continue;

                                var c = colors[ni];
                                r += c.r;
                                g += c.g;
                                b += c.b;
                                a += c.a;
                                w += 1f;
                            }
                        }

                        if (w <= 0f) continue;

                        var inv = 1f / w;
                        var col = new Color(r * inv, g * inv, b * inv, a * inv);

                        if (isNormalMap)
                        {
                            // Averaging encoded normals denormalises them; re-normalise so the
                            // padding still decodes to a unit vector.
                            // 对编码后的法线求平均会破坏单位长度；
                            // 重新归一化使填充区域仍能解码为单位向量。
                            var v = new Vector3(col.r * 2f - 1f, col.g * 2f - 1f, col.b * 2f - 1f);
                            if (v.sqrMagnitude > 1e-8f)
                            {
                                v = v.normalized;
                                col = new Color(
                                    v.x * 0.5f + 0.5f, v.y * 0.5f + 0.5f, v.z * 0.5f + 0.5f, col.a);
                            }
                        }

                        // Padding must never introduce visible opacity.
                        // 填充绝不能引入可见的不透明度。
                        col.a = 0f;

                        pending[i] = col;
                        next[i] = 1f;
                        changed = true;
                    }
                }

                if (!changed) break;

                for (var i = 0; i < colors.Length; i++)
                {
                    if (next[i] > 0f && current[i] <= 0f) colors[i] = pending[i];
                }

                Array.Copy(next, current, next.Length);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_pullPush != null)
            {
                UnityEngine.Object.DestroyImmediate(_pullPush);
                _pullPush = null;
            }
        }
    }
}
