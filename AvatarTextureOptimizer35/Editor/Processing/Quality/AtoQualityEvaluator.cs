using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Per-(island, texture) evaluation context: the reference data at native resolution and the
    /// resample/evaluate pipeline. All metrics compare the candidate (resized down and bilinearly
    /// upsampled back) against the original within the island mask, in linear space. /
    /// 每个（岛, 贴图）的评估上下文：原生分辨率的参考数据 + 重采样/评估管线。全部指标都在线性空间、
    /// 岛掩码内，把缩小后的覆盖区双线性上采样回原尺寸后与原图比较。
    /// </summary>
    internal sealed class AtoTextureEvaluation : IDisposable
    {
        public Texture2D Texture;
        public AtoIsland Island;
        public AtoUvGroup UvGroup;

        /// <summary>Island pixel bbox (in translated UV space, texture pixel coords). / 岛像素包围盒（平移后 UV 空间，贴图像素坐标）。</summary>
        public int BboxX, BboxY, BboxWidth, BboxHeight;

        public TextureWrapMode WrapU;
        public TextureWrapMode WrapV;

        /// <summary>Raw source pixels of the whole texture. / 整张贴图的原始像素。</summary>
        public Color32[] RawPixels;

        /// <summary>Wrap-sampled crop of the island bbox (same layout as the reference). / 岛包围盒的 wrap 采样裁剪区（与参考同布局）。</summary>
        public Color32[] CropPixels;

        /// <summary>Occupancy mask at bbox resolution. / 包围盒分辨率下的占用掩码。</summary>
        public NativeArray<byte> Mask;

        /// <summary>Linear premultiplied reference (bbox resolution; outside mask = 0). / 线性预乘参考（包围盒分辨率；掩码外为 0）。</summary>
        public NativeArray<float4> Reference;

        /// <summary>Linear non-premultiplied reference (normal evaluation). / 线性非预乘参考（法线评估）。</summary>
        public NativeArray<float4> ReferenceStraight;

        /// <summary>Whether the island is a solid color on this texture. / 该岛在此贴图上是否纯色。</summary>
        public bool IsSolid;

        /// <summary>Which metrics are required (union of all usages of this texture). / 需要的指标（该贴图全部用法的并集）。</summary>
        public bool NeedMsSsim;
        public bool NeedDeltaE;
        public bool NeedNormal;
        public bool NeedGray;
        public int GrayChannels;
        public bool NeedCutout;
        public bool NeedBlendRmse;
        public List<(Material material, float cutoff)> CutoutThresholds = new List<(Material, float)>();
        public bool Premultiply;
        public bool Srgb;

        /// <summary>Metric report of the last evaluation. / 最近一次评估的指标报告。</summary>
        public AtoMetricReport LastReport;

        public int PixelCount
        {
            get
            {
                var count = 0;
                for (var i = 0; i < Mask.Length; i++) count += Mask[i];
                return count;
            }
        }

        public void Dispose()
        {
            if (Mask.IsCreated) Mask.Dispose();
            if (Reference.IsCreated) Reference.Dispose();
            if (ReferenceStraight.IsCreated) ReferenceStraight.Dispose();
        }
    }

    /// <summary>
    /// Metric values of one evaluation. / 一次评估的指标值。
    /// </summary>
    internal struct AtoMetricReport
    {
        public float MsSsim;
        public float DeltaE;
        public float CutoutIou;
        public float BlendAlphaRmse;
        public float NormalAngleMean;
        public float NormalAngleP95;
        public float GrayRmse;
        public bool MsSsimSkipped; // island too small for the metric. / 岛过小，跳过该指标。
    }

    /// <summary>
    /// Quality evaluation: builds references, resamples candidates (Burst-accelerated, linear
    /// space, premultiplied alpha) and runs all required metrics. GPU is used for texture
    /// readback; the metric kernels run on CPU Burst jobs (parallel). /
    /// 质量评估：构建参考、重采样候选（Burst 加速、线性空间、预乘 alpha）并执行全部所需指标。
    /// GPU 用于贴图读回；指标核在 CPU Burst 作业上并行执行。
    /// </summary>
    internal sealed class AtoQualityEvaluator
    {
        private readonly AtoContext _ctx;

        public AtoQualityEvaluator(AtoContext ctx)
        {
            _ctx = ctx;
        }

        /// <summary>Get (cached) raw pixels of a texture (shared cache). / 获取（共享缓存）贴图原始像素。</summary>
        public Color32[] GetRawPixels(Texture2D texture) => _ctx.PixelCache.Get(texture);

        /// <summary>
        /// Prepare the reference data for (island, texture). / 准备（岛, 贴图）的参考数据。
        /// </summary>
        public AtoTextureEvaluation Prepare(Texture2D texture, AtoIsland island, AtoUvGroup uvGroup)
        {
            var eval = new AtoTextureEvaluation
            {
                Texture = texture,
                Island = island,
                UvGroup = uvGroup,
                WrapU = texture.wrapModeU,
                WrapV = texture.wrapModeV,
            };

            // Translated UV bbox → pixel bbox. / 平移后的 UV 包围盒 → 像素包围盒。
            var t = island.NormalizationTranslation;
            var minU = island.UvMin.x + t.x;
            var minV = island.UvMin.y + t.y;
            var maxU = island.UvMax.x + t.x;
            var maxV = island.UvMax.y + t.y;
            eval.BboxX = Mathf.FloorToInt(minU * texture.width);
            eval.BboxY = Mathf.FloorToInt(minV * texture.height);
            eval.BboxWidth = Mathf.Clamp(Mathf.CeilToInt(maxU * texture.width) - eval.BboxX, 1, texture.width);
            eval.BboxHeight = Mathf.Clamp(Mathf.CeilToInt(maxV * texture.height) - eval.BboxY, 1, texture.height);
            eval.BboxX = Mathf.Clamp(eval.BboxX, 0, texture.width - eval.BboxWidth);
            eval.BboxY = Mathf.Clamp(eval.BboxY, 0, texture.height - eval.BboxHeight);

            eval.RawPixels = GetRawPixels(texture);

            // ---- metric requirements from the texture's usages ----
            foreach (var record in _ctx.Textures.Values)
            {
                if (record.Texture != texture) continue;
                foreach (var slot in record.Slots)
                {
                    if (!slot.AssignedSlots.Any(pos => pos.renderer == uvGroup.Renderer)) continue;
                    switch (slot.Usage.Kind)
                    {
                        case AtoTextureKind.Main:
                            eval.NeedMsSsim = true;
                            eval.NeedDeltaE = true;
                            eval.Premultiply = true;
                            eval.Srgb |= slot.Usage.Srgb;
                            if (slot.Usage.HasBlend) eval.NeedBlendRmse = true;
                            if (slot.Usage.CutoutThresholds.Count > 0)
                            {
                                eval.NeedCutout = true;
                                foreach (var c in slot.Usage.CutoutThresholds)
                                {
                                    if (!eval.CutoutThresholds.Contains(c)) eval.CutoutThresholds.Add(c);
                                }
                            }
                            break;
                        case AtoTextureKind.Normal:
                            eval.NeedNormal = true;
                            eval.Srgb |= slot.Usage.Srgb;
                            break;
                        case AtoTextureKind.Mask:
                            eval.NeedGray = true;
                            eval.GrayChannels |= slot.Usage.UsedChannels;
                            eval.Srgb |= slot.Usage.Srgb;
                            break;
                        case AtoTextureKind.Tangent:
                            // Tangent data: same metrics as masks (grayscale-ish per channel usage). /
                            // 切线数据：与蒙版同类指标（按使用通道）。
                            eval.NeedGray = true;
                            eval.GrayChannels |= slot.Usage.UsedChannels;
                            eval.Srgb |= slot.Usage.Srgb;
                            break;
                    }
                }
            }
            // If nothing is required (unknown usage), the texture is whitelisted anyway. /
            // 若无需任何指标（未知用法），该贴图本来就已被白名单。
            if (!eval.NeedMsSsim && !eval.NeedDeltaE && !eval.NeedNormal && !eval.NeedGray &&
                !eval.NeedCutout && !eval.NeedBlendRmse)
            {
                eval.NeedMsSsim = true; // safety default: color metrics. / 安全默认：颜色指标。
                eval.NeedDeltaE = true;
                eval.Premultiply = true;
            }

            // ---- mask at bbox resolution ----
            var uvs = new List<Vector2>();
            uvGroup.Mesh.GetUVs(uvGroup.Channel, uvs);
            var mask = new NativeArray<byte>(eval.BboxWidth * eval.BboxHeight, Allocator.Persistent);
            var maskManaged = new byte[eval.BboxWidth * eval.BboxHeight];
            AtoRasterizer.Rasterize(uvs, island.Triangles, Vector2.zero, Vector2.one,
                eval.BboxWidth, eval.BboxHeight, maskManaged,
                new Vector2(t.x, t.y));
            mask.CopyFrom(maskManaged);
            eval.Mask = mask;

            // ---- reference: linear premultiplied / straight ----
            var reference = new NativeArray<float4>(eval.BboxWidth * eval.BboxHeight, Allocator.Persistent);
            var referenceStraight = new NativeArray<float4>(eval.BboxWidth * eval.BboxHeight, Allocator.Persistent);

            var source = new NativeArray<Color32>(eval.BboxWidth * eval.BboxHeight, Allocator.TempJob);
            try
            {
                BuildCrop(eval, source);
                eval.CropPixels = source.ToArray();
                var convert = new AtoBurstKernels.ConvertToLinearJob
                {
                    Source = source,
                    Mask = mask,
                    Srgb = eval.Srgb,
                    Premultiply = true,
                    Output = reference,
                };
                convert.Run(eval.BboxWidth * eval.BboxHeight);
                var convertStraight = new AtoBurstKernels.ConvertToLinearJob
                {
                    Source = source,
                    Mask = mask,
                    Srgb = eval.Srgb,
                    Premultiply = false,
                    Output = referenceStraight,
                };
                convertStraight.Run(eval.BboxWidth * eval.BboxHeight);
            }
            finally
            {
                source.Dispose();
            }
            eval.Reference = reference;
            eval.ReferenceStraight = referenceStraight;

            // ---- solid color check (on raw stored values, per channel) ----
            eval.IsSolid = CheckSolid(eval);

            return eval;
        }

        /// <summary>
        /// Build the crop of the island bbox with wrap-aware sampling (Repeat/Mirror/Clamp). /
        /// 构建岛包围盒的裁剪区（按 Repeat/Mirror/Clamp 采样）。
        /// </summary>
        private void BuildCrop(AtoTextureEvaluation eval, NativeArray<Color32> output)
        {
            var texture = eval.Texture;
            var raw = eval.RawPixels;
            var w = texture.width;
            var h = texture.height;

            int WrapCoord(int x, int size, TextureWrapMode wrap)
            {
                if (wrap == TextureWrapMode.Clamp) return Mathf.Clamp(x, 0, size - 1);
                if (wrap == TextureWrapMode.Mirror)
                {
                    var period = size * 2;
                    x %= period;
                    if (x < 0) x += period;
                    return x < size ? x : period - 1 - x;
                }
                x %= size;
                if (x < 0) x += size;
                return x;
            }

            for (var y = 0; y < eval.BboxHeight; y++)
            {
                for (var x = 0; x < eval.BboxWidth; x++)
                {
                    var sx = WrapCoord(eval.BboxX + x, w, eval.WrapU);
                    var sy = WrapCoord(eval.BboxY + y, h, eval.WrapV);
                    output[y * eval.BboxWidth + x] = raw[sy * w + sx];
                }
            }
        }

        private static bool CheckSolid(AtoTextureEvaluation eval)
        {
            var first = new Color32(0, 0, 0, 0);
            var hasFirst = false;
            for (var i = 0; i < eval.Mask.Length; i++)
            {
                if (eval.Mask[i] == 0) continue;
                var px = eval.CropPixels[i]; // already wrap-sampled. / 已按 wrap 采样。
                if (!hasFirst)
                {
                    first = px;
                    hasFirst = true;
                }
                else if (px.r != first.r || px.g != first.g || px.b != first.b || px.a != first.a)
                {
                    return false;
                }
            }
            return hasFirst;
        }

        // ------------------------------------------------------------------
        // evaluation
        // ------------------------------------------------------------------

        /// <summary>
        /// Evaluate the candidate at scale (sx, sy). Returns true when ALL required metrics pass. /
        /// 按缩放 (sx, sy) 评估候选。全部所需指标达标时返回 true。
        /// </summary>
        public bool Evaluate(AtoTextureEvaluation eval, float sx, float sy, AtoQualityThresholds thresholds,
            out AtoMetricReport report)
        {
            report = default;

            var targetW = Mathf.Max(1, Mathf.RoundToInt(eval.BboxWidth * sx));
            var targetH = Mathf.Max(1, Mathf.RoundToInt(eval.BboxHeight * sy));

            // 1:1 → trivial pass (reference equals candidate). / 1:1 → 恒通过。
            if (targetW == eval.BboxWidth && targetH == eval.BboxHeight)
            {
                report.MsSsim = 1f;
                report.CutoutIou = 1f;
                report.DeltaE = 0f;
                report.BlendAlphaRmse = 0f;
                report.NormalAngleMean = 0f;
                report.NormalAngleP95 = 0f;
                report.GrayRmse = 0f;
                eval.LastReport = report;
                return true;
            }

            // Resize down then up (premultiplied). / 先缩小再放大（预乘）。
            var down = new NativeArray<float4>(targetW * targetH, Allocator.TempJob);
            var up = new NativeArray<float4>(eval.BboxWidth * eval.BboxHeight, Allocator.TempJob);
            var normalDown = new NativeArray<float4>(targetW * targetH, Allocator.TempJob);
            var normalUp = new NativeArray<float4>(eval.BboxWidth * eval.BboxHeight, Allocator.TempJob);
            var decodedRef = new NativeArray<float4>(eval.BboxWidth * eval.BboxHeight, Allocator.TempJob);

            try
            {
                var resize = new AtoBurstKernels.BilinearResizeJob
                {
                    Source = eval.Reference,
                    SourceWidth = eval.BboxWidth,
                    SourceHeight = eval.BboxHeight,
                    DestWidth = targetW,
                    DestHeight = targetH,
                    Output = down,
                };
                resize.Run(targetW * targetH);
                var upscale = new AtoBurstKernels.BilinearResizeJob
                {
                    Source = down,
                    SourceWidth = targetW,
                    SourceHeight = targetH,
                    DestWidth = eval.BboxWidth,
                    DestHeight = eval.BboxHeight,
                    Output = up,
                };
                upscale.Run(eval.BboxWidth * eval.BboxHeight);

                // ---- cheap metrics first (fail fast). / 便宜指标先行（快速失败）。----
                if (eval.NeedCutout)
                {
                    report.CutoutIou = 1f;
                    foreach (var (_, cutoff) in eval.CutoutThresholds)
                    {
                        var result = new NativeArray<float>(2, Allocator.TempJob);
                        try
                        {
                            var job = new AtoBurstKernels.CutoutIoUJob
                            {
                                Ref = eval.Reference,
                                Candidate = up,
                                Mask = eval.Mask,
                                Cutoff = cutoff,
                                Result = result,
                            };
                            job.Run();
                            report.CutoutIou = Mathf.Min(report.CutoutIou, result[0]);
                        }
                        finally
                        {
                            result.Dispose();
                        }
                        if (report.CutoutIou < thresholds.cutoutIou)
                        {
                            eval.LastReport = report;
                            return false;
                        }
                    }
                }

                if (eval.NeedBlendRmse)
                {
                    var result = new NativeArray<float>(2, Allocator.TempJob);
                    try
                    {
                        var job = new AtoBurstKernels.AlphaRmseJob
                        {
                            Ref = eval.Reference,
                            Candidate = up,
                            Mask = eval.Mask,
                            Result = result,
                        };
                        job.Run();
                        report.BlendAlphaRmse = result[0];
                    }
                    finally
                    {
                        result.Dispose();
                    }
                    if (report.BlendAlphaRmse > thresholds.blendAlphaRmse)
                    {
                        eval.LastReport = report;
                        return false;
                    }
                }

                if (eval.NeedGray)
                {
                    var result = new NativeArray<float>(2, Allocator.TempJob);
                    try
                    {
                        var job = new AtoBurstKernels.GrayRmseJob
                        {
                            Ref = eval.Reference,
                            Candidate = up,
                            Mask = eval.Mask,
                            UsedChannels = eval.GrayChannels,
                            Result = result,
                        };
                        job.Run();
                        report.GrayRmse = result[0];
                    }
                    finally
                    {
                        result.Dispose();
                    }
                    if (report.GrayRmse > thresholds.grayscaleRmse)
                    {
                        eval.LastReport = report;
                        return false;
                    }
                }

                if (eval.NeedDeltaE)
                {
                    var result = new NativeArray<float>(2, Allocator.TempJob);
                    try
                    {
                        var job = new AtoBurstKernels.DeltaE00Job
                        {
                            Ref = eval.Reference,
                            Candidate = up,
                            Mask = eval.Mask,
                            Result = result,
                        };
                        job.Run();
                        report.DeltaE = result[0];
                    }
                    finally
                    {
                        result.Dispose();
                    }
                    if (report.DeltaE > thresholds.deltaE00Mean)
                    {
                        eval.LastReport = report;
                        return false;
                    }
                }

                if (eval.NeedNormal)
                {
                    // Decode → resample → compare angles. / 解码 → 重采样 → 角度比较。
                    var decodeRef = new AtoBurstKernels.NormalDecodeJob
                    {
                        Source = eval.ReferenceStraight,
                        Mask = eval.Mask,
                        Output = decodedRef,
                    };
                    decodeRef.Run(eval.BboxWidth * eval.BboxHeight);

                    var resizeNormal = new AtoBurstKernels.BilinearResizeJob
                    {
                        Source = decodedRef,
                        SourceWidth = eval.BboxWidth,
                        SourceHeight = eval.BboxHeight,
                        DestWidth = targetW,
                        DestHeight = targetH,
                        Output = normalDown,
                    };
                    resizeNormal.Run(targetW * targetH);
                    var upscaleNormal = new AtoBurstKernels.BilinearResizeJob
                    {
                        Source = normalDown,
                        SourceWidth = targetW,
                        SourceHeight = targetH,
                        DestWidth = eval.BboxWidth,
                        DestHeight = eval.BboxHeight,
                        Output = normalUp,
                    };
                    upscaleNormal.Run(eval.BboxWidth * eval.BboxHeight);

                    // Renormalize candidate vectors. / 重归一化候选向量。
                    for (var i = 0; i < normalUp.Length; i++)
                    {
                        var v = normalUp[i];
                        var len = math.length(new float3(v.x, v.y, v.z));
                        if (len > 1e-6f) normalUp[i] = new float4(v.x / len, v.y / len, v.z / len, v.w);
                    }

                    var angleResult = new NativeArray<float>(3, Allocator.TempJob);
                    try
                    {
                        var job = new AtoBurstKernels.NormalAngleJob
                        {
                            Ref = decodedRef,
                            Candidate = normalUp,
                            Mask = eval.Mask,
                            Result = angleResult,
                        };
                        job.Run();
                        report.NormalAngleMean = angleResult[0];
                        report.NormalAngleP95 = angleResult[1];
                    }
                    finally
                    {
                        angleResult.Dispose();
                    }

                    if (report.NormalAngleMean > thresholds.normalAngleMean ||
                        report.NormalAngleP95 > thresholds.normalAngleP95)
                    {
                        eval.LastReport = report;
                        return false;
                    }
                }

                if (eval.NeedMsSsim)
                {
                    report.MsSsim = ComputeMsSsim(eval, up, out var skipped);
                    report.MsSsimSkipped = skipped;
                    if (!skipped && report.MsSsim < thresholds.msSsim)
                    {
                        eval.LastReport = report;
                        return false;
                    }
                }

                // ---- extension metrics (after all built-ins pass) ----
                if (AtoExtensionRegistry.QualityMetricProviders.Count > 0)
                {
                    var context = new AtoCustomMetricContext
                    {
                        Mask = eval.Mask.ToArray(),
                        Reference = eval.Reference.ToArray(),
                        Candidate = up.ToArray(),
                        RawPixels = eval.RawPixels,
                        Width = eval.BboxWidth,
                        Height = eval.BboxHeight,
                    };
                    foreach (var provider in AtoExtensionRegistry.QualityMetricProviders)
                    {
                        try
                        {
                            if (!provider.Evaluate(context))
                            {
                                eval.LastReport = report;
                                return false;
                            }
                        }
                        catch (Exception e)
                        {
                            AtoLog.Warn($"[ATO] quality metric provider '{provider.DisplayName}' failed: {e.Message}");
                        }
                    }
                }

                eval.LastReport = report;
                return true;
            }
            finally
            {
                down.Dispose();
                up.Dispose();
                normalDown.Dispose();
                normalUp.Dispose();
                decodedRef.Dispose();
            }
        }

        /// <summary>
        /// MS-SSIM: 5 levels; islands with min side &lt;176px fall back to single-scale SSIM;
        /// min side &lt;11px skips the metric entirely. / MS-SSIM：5 级；包围盒短边&lt;176px 退化为单尺度
        /// SSIM；短边&lt;11px 直接跳过该指标。
        /// </summary>
        private float ComputeMsSsim(AtoTextureEvaluation eval, NativeArray<float4> candidate, out bool skipped)
        {
            skipped = false;
            var minSide = Mathf.Min(eval.BboxWidth, eval.BboxHeight);
            if (minSide < 11)
            {
                skipped = true;
                return 1f;
            }
            if (minSide < 176)
            {
                return ComputeSsim(eval.Reference, candidate, eval.Mask, eval.BboxWidth, eval.BboxHeight, 1.5f, 11);
            }

            // Multi-scale (5 levels). / 多尺度（5 级）。
            var weights = new[] { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
            var product = 1.0;
            var refLevel = eval.Reference;
            var candLevel = candidate;
            var maskLevel = eval.Mask;
            var width = eval.BboxWidth;
            var height = eval.BboxHeight;

            for (var level = 0; level < 5; level++)
            {
                var ssim = ComputeSsim(refLevel, candLevel, maskLevel, width, height, 1.5f, 11);
                if (level == 0)
                {
                    // Level 0: use only the luminance (L) term — contribution as L^α0 × CS terms of others. /
                    // 第 0 级只贡献亮度项 L^α0，其余级贡献 CS 项。
                    var l = ComputeLuminanceTerm(refLevel, candLevel, maskLevel, width, height, 1.5f, 11);
                    product = Math.Pow(l, weights[0]);
                }
                else
                {
                    // Levels 1..4: standard MS-SSIM uses CS terms; we use the full SSIM (≤ CS),
                    // which is slightly stricter (conservative). / 标准 MS-SSIM 的 1..4 级用 CS 项；
                    // 此处用完整 SSIM（≤ CS），略微更严格（保守）。
                    product *= Math.Pow(Math.Max(ssim, 0.0), weights[level]);
                }

                if (level == 4) break;
                // Downsample for the next level. / 为下一级降采样。
                var nw = Mathf.Max(1, width / 2);
                var nh = Mathf.Max(1, height / 2);
                var nextRef = new NativeArray<float4>(nw * nh, Allocator.TempJob);
                var nextCand = new NativeArray<float4>(nw * nh, Allocator.TempJob);
                var nextMask = new NativeArray<byte>(nw * nh, Allocator.TempJob);
                try
                {
                    DownsampleFloat4(refLevel, width, height, nextRef, nw, nh);
                    DownsampleFloat4(candLevel, width, height, nextCand, nw, nh);
                    DownsampleMask(maskLevel, width, height, nextMask, nw, nh);
                }
                catch
                {
                    nextRef.Dispose();
                    nextCand.Dispose();
                    nextMask.Dispose();
                    throw;
                }
                if (level > 0)
                {
                    refLevel.Dispose();
                    candLevel.Dispose();
                    maskLevel.Dispose();
                }
                refLevel = nextRef;
                candLevel = nextCand;
                maskLevel = nextMask;
                width = nw;
                height = nh;
            }

            // Cleanup remaining. / 清理剩余。
            if (!refLevel.Equals(eval.Reference))
            {
                refLevel.Dispose();
                candLevel.Dispose();
                maskLevel.Dispose();
            }
            return (float)Math.Max(0.0, product);
        }

        private static void DownsampleFloat4(NativeArray<float4> source, int w, int h, NativeArray<float4> output,
            int nw, int nh)
        {
            for (var y = 0; y < nh; y++)
            {
                for (var x = 0; x < nw; x++)
                {
                    var x0 = Mathf.Min(x * 2, w - 1);
                    var y0 = Mathf.Min(y * 2, h - 1);
                    var x1 = Mathf.Min(x * 2 + 1, w - 1);
                    var y1 = Mathf.Min(y * 2 + 1, h - 1);
                    output[y * nw + x] = (source[y0 * w + x0] + source[y0 * w + x1] +
                                          source[y1 * w + x0] + source[y1 * w + x1]) * 0.25f;
                }
            }
        }

        private static void DownsampleMask(NativeArray<byte> source, int w, int h, NativeArray<byte> output,
            int nw, int nh)
        {
            for (var y = 0; y < nh; y++)
            {
                for (var x = 0; x < nw; x++)
                {
                    var x0 = Mathf.Min(x * 2, w - 1);
                    var y0 = Mathf.Min(y * 2, h - 1);
                    var x1 = Mathf.Min(x * 2 + 1, w - 1);
                    var y1 = Mathf.Min(y * 2 + 1, h - 1);
                    output[y * nw + x] = (byte)(source[y0 * w + x0] | source[y0 * w + x1] |
                                                source[y1 * w + x0] | source[y1 * w + x1]);
                }
            }
        }

        /// <summary>
        /// Single-scale SSIM (mean over masked pixels, luminance-based). / 单尺度 SSIM（掩码像素均值，基于亮度）。
        /// </summary>
        private float ComputeSsim(NativeArray<float4> reference, NativeArray<float4> candidate,
            NativeArray<byte> mask, int width, int height, float sigma, int window)
        {
            var radius = (window - 1) / 2;
            var size = width * height;
            var lumaRef = new NativeArray<float>(size, Allocator.TempJob);
            var lumaCand = new NativeArray<float>(size, Allocator.TempJob);
            try
            {
                // Luminance channel (linear). / 亮度通道（线性）。
                for (var i = 0; i < size; i++)
                {
                    var r = reference[i];
                    var c = candidate[i];
                    lumaRef[i] = 0.2126f * r.x + 0.7152f * r.y + 0.0722f * r.z;
                    lumaCand[i] = 0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z;
                }

                var muX = new NativeArray<float>(size, Allocator.TempJob);
                var muY = new NativeArray<float>(size, Allocator.TempJob);
                var muXX = new NativeArray<float>(size, Allocator.TempJob);
                var muYY = new NativeArray<float>(size, Allocator.TempJob);
                var muXY = new NativeArray<float>(size, Allocator.TempJob);
                var sqX = new NativeArray<float>(size, Allocator.TempJob);
                var sqY = new NativeArray<float>(size, Allocator.TempJob);
                var xy = new NativeArray<float>(size, Allocator.TempJob);
                try
                {
                    for (var i = 0; i < size; i++)
                    {
                        sqX[i] = lumaRef[i] * lumaRef[i];
                        sqY[i] = lumaCand[i] * lumaCand[i];
                        xy[i] = lumaRef[i] * lumaCand[i];
                    }
                    Blur(lumaRef, muX, width, height, radius, sigma);
                    Blur(lumaCand, muY, width, height, radius, sigma);
                    Blur(sqX, muXX, width, height, radius, sigma);
                    Blur(sqY, muYY, width, height, radius, sigma);
                    Blur(xy, muXY, width, height, radius, sigma);

                    var map = new NativeArray<float>(size, Allocator.TempJob);
                    try
                    {
                        var job = new AtoBurstKernels.SsimMapJob
                        {
                            MuX = muX,
                            MuY = muY,
                            MuXX = muXX,
                            MuYY = muYY,
                            MuXY = muXY,
                            Mask = mask,
                            Map = map,
                        };
                        job.Run(size);
                        var sum = 0.0;
                        var count = 0;
                        for (var i = 0; i < size; i++)
                        {
                            if (mask[i] == 0) continue;
                            sum += map[i];
                            count++;
                        }
                        return count > 0 ? (float)(sum / count) : 1f;
                    }
                    finally
                    {
                        map.Dispose();
                    }
                }
                finally
                {
                    muX.Dispose();
                    muY.Dispose();
                    muXX.Dispose();
                    muYY.Dispose();
                    muXY.Dispose();
                    sqX.Dispose();
                    sqY.Dispose();
                    xy.Dispose();
                }
            }
            finally
            {
                lumaRef.Dispose();
                lumaCand.Dispose();
            }
        }

        /// <summary>
        /// Luminance-only term (L) for MS-SSIM level 0. / MS-SSIM 第 0 级的亮度项 L。
        /// </summary>
        private static double ComputeLuminanceTerm(NativeArray<float4> reference, NativeArray<float4> candidate,
            NativeArray<byte> mask, int width, int height, float sigma, int window)
        {
            // μx, μy over masked pixels (unblurred approximation with Gaussian blur for accuracy). /
            // 掩码像素上的 μx、μy（高斯模糊近似）。
            var radius = (window - 1) / 2;
            var size = width * height;
            var lumaRef = new NativeArray<float>(size, Allocator.TempJob);
            var lumaCand = new NativeArray<float>(size, Allocator.TempJob);
            var muX = new NativeArray<float>(size, Allocator.TempJob);
            var muY = new NativeArray<float>(size, Allocator.TempJob);
            try
            {
                for (var i = 0; i < size; i++)
                {
                    var r = reference[i];
                    var c = candidate[i];
                    lumaRef[i] = 0.2126f * r.x + 0.7152f * r.y + 0.0722f * r.z;
                    lumaCand[i] = 0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z;
                }
                Blur(lumaRef, muX, width, height, radius, sigma);
                Blur(lumaCand, muY, width, height, radius, sigma);

                var sumNum = 0.0;
                var sumDen = 0.0;
                var count = 0;
                var c1 = 0.01f * 0.01f;
                for (var i = 0; i < size; i++)
                {
                    if (mask[i] == 0) continue;
                    var num = 2f * muX[i] * muY[i] + c1;
                    var den = muX[i] * muX[i] + muY[i] * muY[i] + c1;
                    sumNum += num;
                    sumDen += den;
                    count++;
                }
                if (count == 0) return 1.0;
                var l = (sumNum / count) / (sumDen / count);
                return Math.Max(0.0, Math.Min(1.0, l));
            }
            finally
            {
                lumaRef.Dispose();
                lumaCand.Dispose();
                muX.Dispose();
                muY.Dispose();
            }
        }

        private static void Blur(NativeArray<float> source, NativeArray<float> output, int width, int height,
            int radius, float sigma)
        {
            var kernel = new NativeArray<float>(2 * radius + 1, Allocator.TempJob);
            try
            {
                var job = new AtoBurstKernels.GaussianBlurJob
                {
                    Source = source,
                    Output = output,
                    Width = width,
                    Height = height,
                    Radius = radius,
                    Sigma = sigma,
                    Kernel = kernel,
                };
                job.Run();
            }
            finally
            {
                kernel.Dispose();
            }
        }
    }
}
