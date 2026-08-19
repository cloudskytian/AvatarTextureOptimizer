// AtlasBaker.cs
// Bakes atlas textures from placements: per-island resampled content written into rects
// (with rotation), aux-atlas POT downscale, pull-push bleed, texture creation, mip
// streaming + clamp + no read/write, debug PNG saving.
// 烘焙图集:逐岛重采样写入矩形(含旋转)、辅助层POT缩放、pull-push渗色、创建贴图、
// MipStreaming+Clamp+关闭Read/Write、调试PNG保存。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato
{
    internal sealed partial class ATOProcessor
    {
        internal const string AtlasPrefix = "ATO_";

        private void BakeAtlases()
        {
            int total = _d.AtlasPlans.Count;
            int done = 0;
            foreach (var plan in _d.AtlasPlans)
            {
                Tick($"ATO: baking atlases ({done}/{total})", 0.7f + 0.15f * done / Mathf.Max(1, total));
                done++;
                BakeOneAtlas(plan);
            }

            // texture → atlas index / 贴图→图集索引
            foreach (var plan in _d.AtlasPlans)
                foreach (var pi in plan.Placed)
                    _d.AtlasByTexture[pi.Source] = plan;

            ATOLog.Info($"baked {_d.AtlasPlans.Count} atlas textures");
        }

        private void BakeOneAtlas(AtlasPlan plan)
        {
            // ---------- aux downscale factor / 辅助层缩放 ----------
            float auxK = 1f;
            if (plan.Role != TexRole.Color)
            {
                auxK = ComputeAuxScale(plan);
                if (auxK < 1f) ATOLog.V($"atlas '{plan.Name}': aux scale {auxK:0.###} ({plan.Role})");
            }
            plan.AuxScale = auxK;

            int w = Mathf.Max(64, Mathf.RoundToInt(plan.Width * auxK));
            int h = Mathf.Max(64, Mathf.RoundToInt(plan.Height * auxK));
            w = SnapDim(w);
            h = SnapDim(h);

            var pixels = new NativeArray<Color32>(w * h, Allocator.TempJob);
            var valid = new NativeArray<float>(w * h, Allocator.TempJob);
            try
            {
                foreach (var pi in plan.Placed)
                    DrawIslandIntoAtlas(pixels, valid, w, h, pi, auxK);

                // Alpha relevance decided from VALID pixels only (before bleed fills empty).
                // 是否含 alpha 仅由有效像素判定(渗色填充空白之前)。
                plan.HasAlpha = HasAlphaContent(pixels, valid);

                PullPushBleed(pixels, valid, w, h);

                // Transparent atlases: empty-area alpha forced to 0 (spec). / 透明图集:空白区 alpha 强制为0(规范)。
                if (plan.HasAlpha)
                {
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        if (valid[i] <= 0f)
                        {
                            var c = pixels[i];
                            pixels[i] = new Color32(c.r, c.g, c.b, 0);
                        }
                    }
                }

                plan.Width = w;
                plan.Height = h;

                var tex = UploadAtlas(plan, pixels, w, h);
                plan.Baked = tex;

                if (_d.Component.debugSaveAtlases) DebugSaveAtlas(plan, pixels, w, h);
            }
            finally
            {
                pixels.Dispose();
                valid.Dispose();
            }
        }

        /// <summary>Snap aux atlas dims to valid edges. / 辅助图集尺寸规整。</summary>
        private int SnapDim(int v)
        {
            int maxEdge = AvatarTextureOptimizer.MaxAtlasEdge(_d.Platform);
            if (_d.EffectiveProfile.experimentalNpotAtlas)
                return Mathf.Clamp(Mathf.CeilToInt(v / 64f) * 64, 64, maxEdge);
            int p = 64;
            while (p < v && p < maxEdge) p *= 2;
            return Mathf.Min(p, maxEdge);
        }

        /// <summary>
        /// Smallest POT aux scale (≤1) such that every island still passes its aux-only
        /// thresholds; min-padding constraint respected (padding*k ≥ 4px).
        /// / 最小的可用 POT 辅助缩放(≤1),保证所有岛通过辅助层自身阈值;且满足最小 padding。
        /// </summary>
        private float ComputeAuxScale(AtlasPlan plan)
        {
            int pad = EffectivePadding();
            float kMin = 4f / pad;
            if (kMin >= 1f) return 1f;

            float best = 1f;
            float k = 0.5f;
            while (k >= kMin - 1e-4f)
            {
                if (!AuxPassesAt(plan, k)) break;
                best = k;
                k *= 0.5f;
            }
            return best;
        }

        private bool AuxPassesAt(AtlasPlan plan, float k)
        {
            foreach (var pi in plan.Placed)
            {
                var iref = new IslandRef(pi.SetId, pi.IslandId);
                List<TextureNode> textures;
                if (!_d.IslandTextures.TryGetValue(iref.Key, out textures)) continue;
                var island = _d.IslandSets[iref.SetId].Islands[iref.IslandId];
                foreach (var node in textures)
                {
                    if (node.PrimaryRole != plan.Role) continue;
                    float srcBw = pi.SourceUvBounds.width * node.Tex.width;
                    float srcBh = pi.SourceUvBounds.height * node.Tex.height;
                    if (srcBw < 1f || srcBh < 1f) continue;
                    float sx = pi.Rect.width * k / srcBw;
                    float sy = pi.Rect.height * k / srcBh;
                    if (sx > 1f) sx = 1f;
                    if (sy > 1f) sy = 1f;
                    if (!EvaluateTextureQuality(iref, island, _d.IslandSets[iref.SetId], node, sx, sy))
                        return false;
                }
            }
            return true;
        }

        private void DrawIslandIntoAtlas(NativeArray<Color32> atlas, NativeArray<float> valid,
            int atlasW, int atlasH, PlacedIsland pi, float auxK)
        {
            // Rect is in original family space; scale by auxK. / 矩形在原族空间;按 auxK 缩放。
            int rx = Mathf.RoundToInt(pi.Rect.x * auxK);
            int ry = Mathf.RoundToInt(pi.Rect.y * auxK);
            int rw = Mathf.Max(1, Mathf.RoundToInt(pi.Rect.width * auxK));
            int rh = Mathf.Max(1, Mathf.RoundToInt(pi.Rect.height * auxK));
            if (rx + rw > atlasW) rw = atlasW - rx;
            if (ry + rh > atlasH) rh = atlasH - ry;
            if (rw <= 0 || rh <= 0) return;

            var rb = ATOGpu.Instance.Readback(pi.Source);
            var island = _d.IslandSets[pi.SetId].Islands[pi.IslandId];
            AtlasPlan plan = null;
            foreach (var p in _d.AtlasPlans) if (p.Placed.Contains(pi)) { plan = p; break; }

            int bx = Mathf.Clamp(Mathf.FloorToInt(pi.SourceUvBounds.xMin * rb.Width), 0, rb.Width - 1);
            int by = Mathf.Clamp(Mathf.FloorToInt(pi.SourceUvBounds.yMin * rb.Height), 0, rb.Height - 1);
            int bw = Mathf.Clamp(Mathf.CeilToInt(pi.SourceUvBounds.width * rb.Width), 1, rb.Width - bx);
            int bh = Mathf.Clamp(Mathf.CeilToInt(pi.SourceUvBounds.height * rb.Height), 1, rb.Height - by);
            if (bw <= 0 || bh <= 0) return;

            var covMask = GetPixelCoverage(new IslandRef(pi.SetId, pi.IslandId), island, _d.IslandSets[pi.SetId], rb.Width, rb.Height);
            var coverage = new NativeArray<byte>(bw * bh, Allocator.TempJob);
            try
            {
                for (int i = 0; i < coverage.Length && i < covMask.Bytes.Length; i++) coverage[i] = covMask.Bytes[i];

                int tw, th;
                if (pi.Rotated) { tw = rh; th = rw; }
                else { tw = rw; th = rh; }

                float sx = (float)tw / bw;
                float sy = (float)th / bh;

                bool isNormal = false;
                {
                    TextureNode node;
                    if (_d.TextureNodes.TryGetValue(pi.Source, out node)) isNormal = node.PrimaryRole == TexRole.Normal;
                }

                if (isNormal)
                {
                    DrawNormalIsland(rb, bx, by, bw, bh, tw, th, coverage, atlas, rx, ry, pi.Rotated, atlasW);
                }
                else
                {
                    bool srgb = true;
                    {
                        TextureNode node;
                        if (_d.TextureNodes.TryGetValue(pi.Source, out node)) srgb = node.Srgb;
                    }
                    // color path writes directly into the atlas sub-rect / 颜色路径直接写入图集子矩形
                    var down = new AreaDownsampleJob
                    {
                        Source = rb.Pixels,
                        SrcW = rb.Width, SrcH = rb.Height,
                        Coverage = coverage, CovW = bw, CovH = bh,
                        Bbox = new float4(bx, by, bw, bh),
                        ScaleX = sx, ScaleY = sy,
                        ToLinear = srgb,
                        DstW = tw, DstH = th,
                        DstStride = atlasW, DstOffsetX = rx, DstOffsetY = ry,
                        Rotate = pi.Rotated,
                        Target = atlas,
                    };
                    down.Schedule().Complete();
                }

                MarkValid(valid, atlasW, rx, ry, rw, rh, pi.Rotated, coverage, bw, bh, tw, th);
            }
            finally
            {
                coverage.Dispose();
            }
        }

        private void DrawNormalIsland(GpuReadback rb, int bx, int by, int bw, int bh, int tw, int th,
            NativeArray<byte> coverage, NativeArray<Color32> atlas, int rx, int ry, bool rotated, int atlasW)
        {
            var region = ExtractRegion(rb, bx, by, bw, bh);
            var vecA = new NativeArray<float3>(bw * bh, Allocator.TempJob);
            var smallV = new NativeArray<float3>(tw * th, Allocator.TempJob);
            var encoded = new NativeArray<Color32>(tw * th, Allocator.TempJob);
            try
            {
                var dec = new DecodeNormalsJob { Source = region, Count = bw * bh, Normals = vecA };
                dec.Schedule().Complete();
                var vd = new VectorDownsampleJob
                {
                    Source = vecA, SrcW = bw, SrcH = bh, Coverage = coverage,
                    DstW = tw, DstH = th, Target = smallV,
                };
                vd.Schedule().Complete();
                var enc = new EncodeNormalsJob { Normals = smallV, Count = tw * th, Target = encoded };
                enc.Schedule().Complete();

                for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                {
                    var c = encoded[y * tw + x];
                    if (rotated) atlas[(ry + x) * atlasW + rx + y] = c;
                    else atlas[(ry + y) * atlasW + rx + x] = c;
                }
            }
            finally
            {
                region.Dispose();
                vecA.Dispose();
                smallV.Dispose();
                encoded.Dispose();
            }
        }

        private void MarkValid(NativeArray<float> valid, int atlasW, int rx, int ry, int rw, int rh,
            bool rotated, NativeArray<byte> coverage, int bw, int bh, int tw, int th)
        {
            // rw/rh = atlas-space rect dims; tw/th = source-space target dims. / rw/rh 图集矩形;tw/th 源空间尺寸。
            for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                // atlas-space (x,y) → source-space (su,sv) / 图集坐标 → 源空间坐标
                float su, sv;
                if (rotated) { su = y + 0.5f; sv = x + 0.5f; } // transposed / 转置
                else { su = x + 0.5f; sv = y + 0.5f; }
                int cx = Mathf.Clamp(Mathf.FloorToInt(su * bw / tw), 0, bw - 1);
                int cy = Mathf.Clamp(Mathf.FloorToInt(sv * bh / th), 0, bh - 1);
                if (coverage[cy * bw + cx] != 0)
                    valid[(ry + y) * atlasW + rx + x] = 1f;
            }
        }

        private bool HasAlphaContent(NativeArray<Color32> pixels, NativeArray<float> valid)
        {
            for (int i = 0; i < pixels.Length; i += 7)
                if (valid[i] > 0f && pixels[i].a < 250) return true;
            return false;
        }

        private Texture2D UploadAtlas(AtlasPlan plan, NativeArray<Color32> pixels, int w, int h)
        {
            bool linear = !plan.Srgb;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, linear)
            {
                name = plan.Name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = plan.Filter,
                anisoLevel = 4,
            };
            tex.SetPixelData(pixels, 0);
            tex.Apply(true, false);
            _d.Ctx.AssetSaver.SaveAsset(tex);
            ATOLog.V($"atlas '{plan.Name}' {w}x{h} role={plan.Role} util={plan.Utilization:P1} islands={plan.Placed.Count}");
            return tex;
        }

        /// <summary>Pull-push bleed (infinite outward extension). / pull-push 渗色(无限外扩)。</summary>
        private void PullPushBleed(NativeArray<Color32> pixels, NativeArray<float> valid, int w, int h)
        {
            var levelColors = new List<NativeArray<Color32>>();
            var levelWeights = new List<NativeArray<float>>();
            var levelDims = new List<int[]>(); // [w,h] per level / 每层 [w,h]

            int lw = w, lh = h;
            try
            {
                // pull / 下拉
                while (lw > 2 && lh > 2)
                {
                    int nw = Mathf.Max(1, lw / 2), nh = Mathf.Max(1, lh / 2);
                    var dc = new NativeArray<Color32>(nw * nh, Allocator.TempJob);
                    var dw = new NativeArray<float>(nw * nh, Allocator.TempJob);
                    var srcC = levelColors.Count == 0 ? pixels : levelColors[levelColors.Count - 1];
                    var srcW = levelWeights.Count == 0 ? valid : levelWeights[levelWeights.Count - 1];
                    var job = new PullPushDownJob
                    {
                        SrcColor = srcC, SrcWeight = srcW, SrcW = lw, SrcH = lh,
                        DstColor = dc, DstWeight = dw, DstW = nw, DstH = nh,
                    };
                    job.Schedule().Complete();
                    levelColors.Add(dc);
                    levelWeights.Add(dw);
                    levelDims.Add(new[] { nw, nh });
                    lw = nw; lh = nh;
                }

                // push / 上推
                for (int i = levelColors.Count - 1; i >= 1; i--)
                {
                    var fineC = levelColors[i - 1];
                    var fineW = levelWeights[i - 1];
                    int fw = levelDims[i - 1][0], fh = levelDims[i - 1][1];
                    int cw = levelDims[i][0], ch = levelDims[i][1];
                    var up = new PullPushUpJob
                    {
                        FineColor = fineC, FineWeight = fineW, FineW = fw, FineH = fh,
                        CoarseColor = levelColors[i], CoarseWeight = levelWeights[i],
                        CoarseW = cw, CoarseH = ch,
                    };
                    up.Schedule().Complete();
                }
                if (levelColors.Count > 0)
                {
                    var up0 = new PullPushUpJob
                    {
                        FineColor = pixels, FineWeight = valid, FineW = w, FineH = h,
                        CoarseColor = levelColors[0], CoarseWeight = levelWeights[0],
                        CoarseW = levelDims[0][0], CoarseH = levelDims[0][1],
                    };
                    up0.Schedule().Complete();
                }
            }
            finally
            {
                for (int i = 0; i < levelColors.Count; i++)
                {
                    levelColors[i].Dispose();
                    levelWeights[i].Dispose();
                }
            }
        }

        private TextureNode NodeOf(Texture2D tex)
        {
            TextureNode node;
            return _d.TextureNodes.TryGetValue(tex, out node) ? node : null;
        }

        private void DebugSaveAtlas(AtlasPlan plan, NativeArray<Color32> pixels, int w, int h)
        {
            try
            {
                var dir = "Assets/AvatarTextureOptimizerDebug";
                if (!UnityEditor.AssetDatabase.IsValidFolder(dir))
                    UnityEditor.AssetDatabase.CreateFolder("Assets", "AvatarTextureOptimizerDebug");
                var copy = MakeReadableCopy(pixels, w, h, plan.Srgb);
                try
                {
                    System.IO.File.WriteAllBytes(dir + "/" + plan.Name + ".png", ImageConversion.EncodeToPNG(copy));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(copy);
                }
                ATOLog.Info($"debug atlas saved: {dir}/{plan.Name}.png");
            }
            catch (Exception e)
            {
                ATOLog.Warn($"debug save failed: {e.Message}");
            }
        }

        private Texture2D MakeReadableCopy(NativeArray<Color32> pixels, int w, int h, bool srgb)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, !srgb);
            t.SetPixelData(pixels, 0);
            t.Apply(false, false);
            return t;
        }

        // ================================================================== //
        // Standalone (no-atlas) baking / 独立贴图烘焙(不生成图集)
        // ================================================================== //
        private void BakeStandaloneTextures()
        {
            var all = _d.TextureNodes.Values
                .Where(n => !_d.WhitelistedTextures.Contains(n.Tex))
                .ToList();
            int done = 0;
            foreach (var node in all)
            {
                Tick($"ATO: baking textures ({done}/{all.Count})", 0.6f + 0.2f * done / Mathf.Max(1, all.Count));
                done++;
                float s;
                if (!_wholeTexScale.TryGetValue(node.Tex, out s)) s = 1f;
                if (s >= 0.999f) continue; // unchanged ref / 引用不变

                var rb = ATOGpu.Instance.Readback(node.Tex);
                int nw = Mathf.Max(1, Mathf.RoundToInt(rb.Width * s));
                int nh = Mathf.Max(1, Mathf.RoundToInt(rb.Height * s));

                var coverage = new NativeArray<byte>(rb.Width * rb.Height, Allocator.TempJob);
                var small = new NativeArray<Color32>(nw * nh, Allocator.TempJob);
                try
                {
                    for (int i = 0; i < coverage.Length; i++) coverage[i] = 1;

                    if (node.PrimaryRole == TexRole.Normal)
                    {
                        BakeStandaloneNormal(rb, nw, nh, node);
                    }
                    else
                    {
                        var down = new AreaDownsampleJob
                        {
                            Source = rb.Pixels, SrcW = rb.Width, SrcH = rb.Height,
                            Coverage = coverage, CovW = rb.Width, CovH = rb.Height,
                            Bbox = new float4(0, 0, rb.Width, rb.Height),
                            ScaleX = s, ScaleY = s, ToLinear = node.Srgb,
                            DstW = nw, DstH = nh, Target = small,
                        };
                        down.Schedule().Complete();

                        var tex = new Texture2D(nw, nh, TextureFormat.RGBA32, true, !node.Srgb)
                        {
                            name = "ATO_" + node.Tex.name,
                            wrapMode = node.Tex.wrapMode,
                            filterMode = node.Tex.filterMode,
                        };
                        tex.SetPixelData(small, 0);
                        tex.Apply(true, false);
                        _d.Ctx.AssetSaver.SaveAsset(tex);
                        _d.TextureReplacements[node.Tex] = tex;
                        _d.StandaloneBaked[node.Tex] = tex;
                    }
                }
                finally
                {
                    coverage.Dispose();
                    small.Dispose();
                }
            }
            ATOLog.Info($"baked {_d.StandaloneBaked.Count} standalone scaled textures");
        }

        private void BakeStandaloneNormal(GpuReadback rb, int nw, int nh, TextureNode node)
        {
            int count = rb.Width * rb.Height;
            var coverage = new NativeArray<byte>(count, Allocator.TempJob);
            var vecA = new NativeArray<float3>(count, Allocator.TempJob);
            var smallV = new NativeArray<float3>(nw * nh, Allocator.TempJob);
            var encoded = new NativeArray<Color32>(nw * nh, Allocator.TempJob);
            try
            {
                for (int i = 0; i < count; i++) coverage[i] = 1;
                var dec = new DecodeNormalsJob { Source = rb.Pixels, Count = count, Normals = vecA };
                dec.Schedule().Complete();
                var vd = new VectorDownsampleJob
                {
                    Source = vecA, SrcW = rb.Width, SrcH = rb.Height, Coverage = coverage,
                    DstW = nw, DstH = nh, Target = smallV,
                };
                vd.Schedule().Complete();
                var enc = new EncodeNormalsJob { Normals = smallV, Count = nw * nh, Target = encoded };
                enc.Schedule().Complete();

                var tex = new Texture2D(nw, nh, TextureFormat.RGBA32, true, true)
                {
                    name = "ATO_" + node.Tex.name,
                    wrapMode = node.Tex.wrapMode,
                    filterMode = node.Tex.filterMode,
                };
                tex.SetPixelData(encoded, 0);
                tex.Apply(true, false);
                _d.Ctx.AssetSaver.SaveAsset(tex);
                _d.TextureReplacements[node.Tex] = tex;
                _d.StandaloneBaked[node.Tex] = tex;
            }
            finally
            {
                coverage.Dispose(); vecA.Dispose(); smallV.Dispose(); encoded.Dispose();
            }
        }
    }
}
