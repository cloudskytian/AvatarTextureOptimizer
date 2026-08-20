// ATOCompositor.cs — 图集合成器 / Atlas compositor.
// 说明：按箱合成各角色图集：
//  - 基础图集（主色/颜色角色）：贴图裁剪按装箱布局缩放/旋转后写入；纯色岛直接填充；近无损原样拷贝
//  - 法线图集：解码 → 缩放 → 重归一化 → 编码（AG 通道，B=1）；蒙版图集同理（线性）
//  - 各角色图集尺寸 = 箱尺寸 × 角色缩放系数（木桶），位置随系数等比缩放（归一化布局不变）
//  - GPU pull-push 外扩填充空白（透明贴图 alpha 保持 0）；超大图集（>4096）使用分块直写并跳过 pull-push（罕见）
// Note: composes per-role atlases per bin: base atlas (main/color) with rotated/scaled crops, solid fills and
// near-lossless copies; normal atlas decoded→scaled→renormalized→encoded (AG channels, B=1); mask atlas linear.
// Role atlas sizes = bin size × role scale factors (barrel); positions scale proportionally (layout preserved).
// GPU pull-push fills gaps (alpha stays 0 for transparent); oversized atlases (>4096) use block writes and skip pull-push (rare).

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>合成结果。/ Composition result.</summary>
    internal sealed class ATOComposedAtlas : IDisposable
    {
        public int width;
        public int height;
        public ATORole role;
        public NativeArray<float4> pixels;

        public void Dispose()
        {
            if (pixels.IsCreated) pixels.Dispose();
        }
    }

    /// <summary>图集合成器。/ Atlas compositor.</summary>
    internal static class ATOCompositor
    {
        /// <summary>单缓冲合成上限（4096²，约 256MB 峰值）。/ Single-buffer composition limit (4096², ~256MB peak).</summary>
        public const int MaxSingleBufferSide = 4096;

        /// <summary>
        /// 合成一个箱的全部角色图集。
        /// Compose all role atlases of a bin.
        /// </summary>
        public static List<ATOComposedAtlas> ComposeBin(ATOBin bin, ATOQualityEvaluator evaluator, ATOGpuMetrics gpu)
        {
            var result = new List<ATOComposedAtlas>();
            var group = bin.group;

            // 基础角色图集 / base role atlas
            var baseAtlas = NewAtlas(bin.width, bin.height, ATORole.Main);
            result.Add(baseAtlas);

            // 法线图集 / normal atlas
            var normalW = Mathf.Max(4, Mathf.RoundToInt(bin.width * bin.normalScaleU / 4f) * 4);
            var normalH = Mathf.Max(4, Mathf.RoundToInt(bin.height * bin.normalScaleV / 4f) * 4);
            ATOComposedAtlas normalAtlas = null;
            ATOComposedAtlas maskAtlas = null;
            var maskW = Mathf.Max(4, Mathf.RoundToInt(bin.width * bin.maskScaleU / 4f) * 4);
            var maskH = Mathf.Max(4, Mathf.RoundToInt(bin.height * bin.maskScaleV / 4f) * 4);
            if (group.hasNormal) { normalAtlas = NewAtlas(normalW, normalH, ATORole.Normal); result.Add(normalAtlas); }
            if (group.hasMask) { maskAtlas = NewAtlas(maskW, maskH, ATORole.Mask); result.Add(maskAtlas); }

            foreach (var item in bin.items)
            {
                foreach (var r in item.refs)
                {
                    var island = PackIslandRegistry.TryGet(r);
                    if (island == null) continue;
                    if (!group.layout.TryGetValue(island, out var placement)) continue;

                    // 岛矩形（像素，4px 对齐）/ island rect (px, 4px aligned)
                    var rectW = Mathf.CeilToInt(Mathf.Max(1f, island.baseSizeU) / 4f) * 4;
                    var rectH = Mathf.CeilToInt(Mathf.Max(1f, island.baseSizeV) / 4f) * 4;
                    var px = Mathf.RoundToInt(placement.min.x * bin.width / 4f) * 4;
                    var py = Mathf.RoundToInt(placement.min.y * bin.height / 4f) * 4;

                    switch (r.category)
                    {
                        case ATOScaleCategory.Normal:
                            if (normalAtlas != null)
                                StampRef(evaluator, r, normalAtlas,
                                    Mathf.RoundToInt(px * bin.normalScaleU), Mathf.RoundToInt(py * bin.normalScaleV),
                                    Mathf.Max(4, Mathf.RoundToInt(rectW * bin.normalScaleU / 4f) * 4),
                                    Mathf.Max(4, Mathf.RoundToInt(rectH * bin.normalScaleV / 4f) * 4),
                                    placement.rotation, true);
                            break;
                        case ATOScaleCategory.Mask:
                            if (maskAtlas != null)
                                StampRef(evaluator, r, maskAtlas,
                                    Mathf.RoundToInt(px * bin.maskScaleU), Mathf.RoundToInt(py * bin.maskScaleV),
                                    Mathf.Max(4, Mathf.RoundToInt(rectW * bin.maskScaleU / 4f) * 4),
                                    Mathf.Max(4, Mathf.RoundToInt(rectH * bin.maskScaleV / 4f) * 4),
                                    placement.rotation, false);
                            break;
                        default:
                            StampRef(evaluator, r, baseAtlas, px, py, rectW, rectH, placement.rotation, false);
                            break;
                    }
                }
            }

            // GPU pull-push 外扩（超大图集跳过）/ GPU pull-push dilation (skipped for oversized atlases)
            foreach (var atlas in result)
            {
                if (atlas.width > MaxSingleBufferSide || atlas.height > MaxSingleBufferSide)
                {
                    ATOLog.Warning($"Atlas {atlas.width}x{atlas.height} exceeds the single-buffer limit; pull-push dilation skipped. (图集超过单缓冲上限，跳过 pull-push 外扩)");
                    continue;
                }
                ApplyPullPush(atlas, gpu);
            }
            return result;
        }

        private static ATOComposedAtlas NewAtlas(int w, int h, ATORole role)
        {
            return new ATOComposedAtlas
            {
                width = w,
                height = h,
                role = role,
                pixels = new NativeArray<float4>(w * h, Allocator.Persistent),
            };
        }

        /// <summary>将一份引用内容写入图集。/ Stamp one ref's content into an atlas.</summary>
        private static void StampRef(ATOQualityEvaluator evaluator, ATOIslandRef r, ATOComposedAtlas atlas,
            int px, int py, int targetW, int targetH, int rotation, bool renormalizeNormals)
        {
            var source = evaluator.GetSourceCrop(r);
            var sw = r.cropRect.width;
            var sh = r.cropRect.height;

            NativeArray<float4> content;
            if (r.losslessCopy && sw == targetW && sh == targetH)
            {
                // 原样拷贝 / plain copy
                content = new NativeArray<float4>(source, Allocator.Temp);
            }
            else if (r.losslessCopy)
            {
                // 原样拷贝（不重采样），贴入目标左上角 / plain copy (no resampling), stamped at the target's top-left
                content = new NativeArray<float4>(targetW * targetH, Allocator.Temp);
                for (int y = 0; y < sh && y < targetH; y++)
                    for (int x = 0; x < sw && x < targetW; x++)
                        content[y * targetW + x] = source[y * sw + x];
            }
            else if (r.pureColor)
            {
                var color = evaluator.GetSolidColor(r);
                content = new NativeArray<float4>(targetW * targetH, Allocator.Temp);
                for (int i = 0; i < content.Length; i++) content[i] = color;
            }
            else
            {
                content = ATOMetrics.Resize(source, sw, sh, targetW, targetH, Allocator.Temp);
                if (renormalizeNormals && r.category == ATOScaleCategory.Normal)
                {
                    for (int i = 0; i < content.Length; i++)
                    {
                        var n = math.normalize(content[i].xyz);
                        content[i] = new float4(n.x, n.y, n.z, 1f);
                    }
                }
            }

            // 合并岛内容偏移 / merged-island content offset
            var ox = Mathf.RoundToInt(r.cropOffset.x * targetW);
            var oy = Mathf.RoundToInt(r.cropOffset.y * targetH);

            // 旋转内容 / rotate content
            var rotated = rotation % 4 == 0 ? content : RotatePixels(content, targetW, targetH, rotation);
            if (rotated != content) content.Dispose();
            content = rotated;

            var cw = rotation % 2 == 1 ? targetH : targetW;
            var ch = rotation % 2 == 1 ? targetW : targetH;

            ATOIslandCrop.StampCrop(atlas.pixels, atlas.width, atlas.height, content, cw, ch, px + ox, py + oy);
            content.Dispose();
        }

        /// <summary>旋转像素缓冲（90 度步进）。/ Rotate a pixel buffer (90° steps).</summary>
        public static NativeArray<float4> RotatePixels(NativeArray<float4> src, int w, int h, int rot)
        {
            var dst = new NativeArray<float4>(src.Length, Allocator.Temp);
            switch (rot & 3)
            {
                case 1:
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            dst[x * h + (h - 1 - y)] = src[y * w + x];
                    return dst;
                case 2:
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            dst[(h - 1 - y) * w + (w - 1 - x)] = src[y * w + x];
                    return dst;
                case 3:
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            dst[(w - 1 - x) * h + y] = src[y * w + x];
                    return dst;
                default:
                    return src;
            }
        }

        /// <summary>GPU pull-push：上传 → 外扩 → 读回。/ GPU pull-push: upload → dilate → read back.</summary>
        private static void ApplyPullPush(ATOComposedAtlas atlas, ATOGpuMetrics gpu)
        {
            if (!gpu.Available) return;
            var rt = new RenderTexture(atlas.width, atlas.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            var tex = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBAFloat, false, true)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            var data = atlas.pixels.ToArray();
            tex.SetPixelData(data, 0);
            tex.Apply(false, false);
            Graphics.Blit(tex, rt);

            var preserveAlpha = atlas.role == ATORole.Main && HasAnyAlpha(tex);
            gpu.PullPush(rt, preserveAlpha);

            var readback = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBAFloat, false, true)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0, 0, atlas.width, atlas.height), 0, 0, false);
            readback.Apply(false, false);
            RenderTexture.active = prev;

            var outPixels = readback.GetPixelData<float4>(0);
            outPixels.CopyTo(atlas.pixels);
            outPixels.Dispose();

            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(readback);
            rt.Release();
        }

        private static bool HasAnyAlpha(Texture2D tex)
        {
            var pixels = tex.GetPixels32(0);
            foreach (var p in pixels)
                if (p.a < 255) return true;
            return false;
        }
    }
}
