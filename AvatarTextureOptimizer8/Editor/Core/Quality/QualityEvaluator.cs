// QualityEvaluator.cs
// Per-island quality-driven scale search: binary search of the minimal passing scale,
// pixel-density clamps, pure-color short-circuit, anisotropic refinement, and the barrel
// rule (max requirement across textures sharing the island).
// 逐岛质量缩放搜索:二分最小通过缩放、像素密度钳制、纯色短路、各向异性细化、
// 木桶效应(同岛多贴图取最严要求)。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.ato
{
    internal sealed partial class ATOProcessor
    {
        private const int ScaleSteps = 128; // scale quantization / 缩放量化步数
        private static readonly float[] GaussianKernel = BuildGaussian();

        private static float[] BuildGaussian()
        {
            var k = new float[11];
            float sum = 0;
            for (int i = 0; i < 11; i++)
            {
                float d = i - 5;
                k[i] = Mathf.Exp(-d * d / (2f * 1.5f * 1.5f));
                sum += k[i];
            }
            for (int i = 0; i < 11; i++) k[i] /= sum;
            return k;
        }

        // ================================================================== //
        // UV groups / UV组
        // ================================================================== //
        private void BuildUVGroups()
        {
            var uf = new AtoUnionFind();
            var islandSlot = new Dictionary<long, int>();
            var texSlot = new Dictionary<TextureNode, int>();

            foreach (var kv in _d.IslandTextures)
            {
                int iSlot;
                if (!islandSlot.TryGetValue(kv.Key, out iSlot)) islandSlot[kv.Key] = iSlot = uf.Add();
                foreach (var node in kv.Value)
                {
                    int tSlot;
                    if (!texSlot.TryGetValue(node, out tSlot)) texSlot[node] = tSlot = uf.Add();
                    uf.Union(iSlot, tSlot);
                }
            }

            var groupsByRoot = new Dictionary<int, UvGroup>();
            foreach (var kv in islandSlot)
            {
                int root = uf.Find(kv.Value);
                UvGroup g;
                if (!groupsByRoot.TryGetValue(root, out g))
                {
                    groupsByRoot[root] = g = new UvGroup { Id = _d.UvGroups.Count };
                    _d.UvGroups.Add(g);
                }
                g.Islands.Add(new IslandRef((int)(kv.Key >> 32), (int)(kv.Key & 0xFFFFFFFF)));
            }
            foreach (var kv in texSlot)
            {
                int root = uf.Find(kv.Value);
                UvGroup g;
                if (groupsByRoot.TryGetValue(root, out g)) g.Textures.Add(kv.Key);
            }

            foreach (var g in _d.UvGroups)
            {
                g.Signature = ComputeSignature(g);
                g.HasVariants = g.Islands.Any(i => _d.IslandTextures[i.Key].Count > 1);
                AssignColorLayers(g);
                if (g.Textures.Any(t => t.NoAtlas)) g.FallbackWhitelist = true;
            }
            ATOLog.V($"UV groups: {_d.UvGroups.Count} components; " +
                     $"{_d.UvGroups.Count(g => g.HasVariants)} with variants");
        }

        private static UvGroupSignature ComputeSignature(UvGroup g)
        {
            var sig = new UvGroupSignature();
            foreach (var n in g.Textures)
            {
                if (n.PrimaryRole == TexRole.Color)
                {
                    sig.HasColor = true;
                    if (!n.Srgb) sig.AnyLinearColor = true;
                    sig.ColorSrgb = n.Srgb;
                    sig.ColorFilter = n.Filter;
                }
                else if (n.PrimaryRole == TexRole.Normal) sig.HasNormal = true;
                else sig.HasMask = true;
            }
            return sig;
        }

        /// <summary>Greedy coloring: textures sharing any island → distinct layers. / 贪心着色:共享任一岛的贴图分到不同层。</summary>
        private void AssignColorLayers(UvGroup g)
        {
            var neighbors = new Dictionary<TextureNode, HashSet<TextureNode>>();
            foreach (var iref in g.Islands)
            {
                List<TextureNode> list;
                if (!_d.IslandTextures.TryGetValue(iref.Key, out list) || list == null) continue;
                foreach (var a in list)
                foreach (var b in list)
                    if (!ReferenceEquals(a, b))
                    {
                        HashSet<TextureNode> set;
                        if (!neighbors.TryGetValue(a, out set)) neighbors[a] = set = new HashSet<TextureNode>();
                        set.Add(b);
                    }
            }

            var used = new Dictionary<TextureNode, int>();
            var ordered = g.Textures.OrderByDescending(t =>
            {
                HashSet<TextureNode> s;
                return neighbors.TryGetValue(t, out s) ? s.Count : 0;
            }).ToList();
            foreach (var n in ordered)
            {
                var banned = new HashSet<int>();
                HashSet<TextureNode> nb;
                if (neighbors.TryGetValue(n, out nb))
                    foreach (var m in nb)
                    {
                        int l;
                        if (used.TryGetValue(m, out l)) banned.Add(l);
                    }
                int layer = 0;
                while (banned.Contains(layer)) layer++;
                used[n] = layer;
            }
            foreach (var kv in used) kv.Key.ColorLayer = kv.Value;
        }

        // ================================================================== //
        // Island scaling / 岛缩放
        // ================================================================== //
        private void ScaleIslands()
        {
            BuildUVGroups();

            var profile = _d.EffectiveProfile;
            bool nearLossless = profile.thresholds.IsNearLossless;
            int totalIslands = 0;
            foreach (var g in _d.UvGroups) totalIslands += g.Islands.Count;
            int done = 0;

            foreach (var g in _d.UvGroups)
            {
                g.ScaleDecisions = new List<IslandScaleDecision>();
                foreach (var iref in g.Islands)
                {
                    Tick($"ATO: scaling islands ({done}/{totalIslands})", 0.05f + 0.45f * done / Mathf.Max(1, totalIslands));
                    done++;
                    var dec = DecideIslandScale(iref, g, nearLossless);
                    g.ScaleDecisions.Add(dec);
                }
            }

            _d.IslandMinScale.Clear();
            foreach (var g in _d.UvGroups)
                foreach (var dec in g.ScaleDecisions)
                    _d.IslandMinScale[ATOBuildData.Key(dec.SetId, dec.IslandId)] = Mathf.Min(dec.Sx, dec.Sy);
            ATOLog.Info($"quality scaling done: {totalIslands} islands (near-lossless: {nearLossless})");
        }

        private IslandScaleDecision DecideIslandScale(IslandRef iref, UvGroup g, bool nearLossless)
        {
            var set = _d.IslandSets[iref.SetId];
            var island = set.Islands[iref.IslandId];
            var dec = new IslandScaleDecision { SetId = iref.SetId, IslandId = iref.IslandId, Sx = 1f, Sy = 1f, RefW = texW, RefH = texH };
            List<TextureNode> textures;
            if (!_d.IslandTextures.TryGetValue(iref.Key, out textures) || textures.Count == 0) return dec;

            int texW = 0, texH = 0;
            foreach (var t in textures)
            {
                if (t.Tex.width > texW) texW = t.Tex.width;
                if (t.Tex.height > texH) texH = t.Tex.height;
            }
            int bboxW = Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.width * texW));
            int bboxH = Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.height * texH));

            if (nearLossless)
            {
                dec.Note = "near-lossless";
                return dec;
            }

            // ---- density bounds / 密度边界 ----
            var profile = _d.EffectiveProfile;
            float minD = (float)(int)profile.minDensity;
            float maxD = (float)(int)profile.maxDensity;
            float covered = island.PixelMask != null ? island.PixelMask.SetCount() : bboxW * bboxH;
            covered = Mathf.Max(1f, covered);
            float density0 = island.WorldArea > 1e-8f ? Mathf.Sqrt(covered / island.WorldArea) : float.MaxValue;
            float lo = density0 <= minD ? 1f : Mathf.Max(1f / ScaleSteps, minD / density0);
            float hi = density0 > maxD ? maxD / density0 : 1f;
            if (lo > 1f) lo = 1f;
            if (hi < lo) hi = lo;

            // ---- pure color short-circuit / 纯色短路 ----
            if (IsPureColor(iref, textures, texW, texH))
            {
                float targetShort = Mathf.Min(4f, Mathf.Min(bboxW, bboxH));
                dec.Sx = Mathf.Clamp(Mathf.Max(targetShort / bboxW, lo), 0f, 1f);
                dec.Sy = Mathf.Clamp(Mathf.Max(targetShort / bboxH, lo), 0f, 1f);
                dec.Note = "pure-color";
                return dec;
            }

            // ---- uniform binary search / 均匀二分 ----
            float sLo = lo, sHi = 1f;
            if (!IslandPasses(iref, textures, 1f, 1f))
            {
                dec.Note = "identity-fail(kept@1)";
                return dec; // rare: identity should always pass / 罕见:原样应当通过
            }
            if (IslandPasses(iref, textures, sLo, sLo))
            {
                sHi = sLo;
            }
            else
            {
                while (sHi - sLo > 1f / ScaleSteps)
                {
                    float mid = 0.5f * (sLo + sHi);
                    if (IslandPasses(iref, textures, mid, mid)) sHi = mid; else sLo = mid;
                }
            }
            float su = sHi;

            // ---- anisotropic refinement / 各向异性细化 ----
            float sx = RefineAxis(iref, textures, lo, su, true, su);
            float sy = RefineAxis(iref, textures, lo, sx, false, su);

            // ---- density cap / 密度上限 ----
            float minAxis = Mathf.Min(sx, sy);
            if (hi < minAxis)
            {
                float k = hi / minAxis;
                sx *= k; sy *= k;
                dec.Note = "density-capped";
            }

            dec.Sx = Mathf.Clamp01(sx);
            dec.Sy = Mathf.Clamp01(sy);
            return dec;
        }

        private float RefineAxis(IslandRef iref, List<TextureNode> textures, float lo, float fixedVal,
            bool xAxis, float uniformPass)
        {
            if (uniformPass <= lo + 1f / ScaleSteps) return uniformPass;
            float aLo = lo;
            if (xAxis)
            {
                if (IslandPasses(iref, textures, aLo, fixedVal)) return aLo;
                float aHi = fixedVal;
                while (aHi - aLo > 1f / ScaleSteps)
                {
                    float mid = 0.5f * (aLo + aHi);
                    if (IslandPasses(iref, textures, mid, fixedVal)) aHi = mid; else aLo = mid;
                }
                return aHi;
            }
            if (IslandPasses(iref, textures, fixedVal, aLo)) return aLo;
            float bHi = fixedVal;
            while (bHi - aLo > 1f / ScaleSteps)
            {
                float mid = 0.5f * (aLo + bHi);
                if (IslandPasses(iref, textures, fixedVal, mid)) bHi = mid; else aLo = mid;
            }
            return bHi;
        }

        // ------------------------------------------------------------------ //
        // Predicate / 谓词
        // ------------------------------------------------------------------ //
        private bool IslandPasses(IslandRef iref, List<TextureNode> textures, float sx, float sy)
        {
            var set = _d.IslandSets[iref.SetId];
            var island = set.Islands[iref.IslandId];
            // sx/sy relate to the group's reference (largest) texture; convert to each
            // texture's own effective scale before evaluating. / sx/sy 以组内最大贴图为基准;
            // 评估前换算为各贴图自身的有效缩放。
            float refW = 1f, refH = 1f;
            foreach (var t in textures)
            {
                if (t.Tex.width > refW) refW = t.Tex.width;
                if (t.Tex.height > refH) refH = t.Tex.height;
            }
            foreach (var node in textures)
            {
                float ex = Mathf.Min(1f, sx * refW / Mathf.Max(1, node.Tex.width));
                float ey = Mathf.Min(1f, sy * refH / Mathf.Max(1, node.Tex.height));
                if (!EvaluateTextureQuality(iref, island, set, node, ex, ey))
                    return false;
            }
            return true;
        }

        private bool EvaluateTextureQuality(IslandRef iref, UvIsland island, IslandSetData set, TextureNode node, float sx, float sy)
        {
            var th = _d.EffectiveProfile.thresholds;
            try
            {
                var rb = ATOGpu.Instance.Readback(node.Tex);
                return EvaluateWithReadback(iref, island, node, rb, sx, sy, th);
            }
            catch (Exception e)
            {
                ATOLog.Warn($"quality eval failed for '{node.Tex.name}': {e.Message}; keeping scale");
                return false;
            }
        }

        private bool EvaluateWithReadback(IslandRef iref, UvIsland island, TextureNode node, GpuReadback rb,
            float sx, float sy, QualityThresholds th)
        {
            int texW = rb.Width, texH = rb.Height;
            var bounds = island.UvBounds;
            int bx = Mathf.Clamp(Mathf.FloorToInt(bounds.xMin * texW), 0, texW - 1);
            int by = Mathf.Clamp(Mathf.FloorToInt(bounds.yMin * texH), 0, texH - 1);
            int bw = Mathf.Clamp(Mathf.CeilToInt(bounds.width * texW), 1, texW - bx);
            int bh = Mathf.Clamp(Mathf.CeilToInt(bounds.height * texH), 1, texH - by);

            var covMask = GetPixelCoverage(iref, island, _d.IslandSets[iref.SetId], texW, texH);
            var coverage = new NativeArray<byte>(bw * bh, Allocator.TempJob);
            try
            {
                // covMask.Bytes is at bbox granularity (cellPx=1) / 覆盖为 bbox 粒度
                for (int i = 0; i < coverage.Length; i++) coverage[i] = covMask.Bytes[i];

                int dstW = Mathf.Max(1, Mathf.RoundToInt(bw * sx));
                int dstH = Mathf.Max(1, Mathf.RoundToInt(bh * sy));

                if (node.PrimaryRole == TexRole.Normal)
                    return EvaluateNormal(rb, bx, by, bw, bh, dstW, dstH, coverage, th);

                var regionA = ExtractRegion(rb, bx, by, bw, bh);
                var small = new NativeArray<Color32>(dstW * dstH, Allocator.TempJob);
                var recon = new NativeArray<Color32>(bw * bh, Allocator.TempJob);
                try
                {
                    var down = new AreaDownsampleJob
                    {
                        Source = rb.Pixels,
                        SrcW = texW, SrcH = texH,
                        Coverage = coverage, CovW = bw, CovH = bh,
                        Bbox = new float4(bx, by, bw, bh),
                        ScaleX = sx, ScaleY = sy, ToLinear = node.Srgb,
                        DstW = dstW, DstH = dstH, Target = small,
                    };
                    down.Schedule().Complete();

                    var up = new BilinearUpsampleJob
                    {
                        Small = small, SmallW = dstW, SmallH = dstH,
                        DstW = bw, DstH = bh, Dst = recon,
                    };
                    up.Schedule().Complete();

                    return MetricsPass(regionA, recon, coverage, bw, bh, node, th);
                }
                finally
                {
                    regionA.Dispose();
                    small.Dispose();
                    recon.Dispose();
                }
            }
            finally
            {
                coverage.Dispose();
            }
        }

        private bool MetricsPass(NativeArray<Color32> a, NativeArray<Color32> b, NativeArray<byte> mask,
            int w, int h, TextureNode node, QualityThresholds th)
        {
            bool ok = true;
            var role = node.PrimaryRole;

            if (role == TexRole.Color && node.Srgb)
            {
                if (!RunSsim(a, b, mask, w, h, th.msSsimMin)) ok = false;

                if (ok)
                {
                    var res = new NativeArray<float>(1, Allocator.TempJob);
                    try
                    {
                        var job = new DeltaE2000Job { A = a, B = b, Mask = mask, W = w, H = h, Result = res };
                        job.Schedule().Complete();
                        if (res[0] < float.MaxValue && res[0] > th.deltaEMax) ok = false;
                    }
                    finally { res.Dispose(); }
                }

                if (ok)
                {
                    bool hasAlphaReq = false;
                    foreach (var u in node.Usages)
                        if (u.Alpha != AlphaMode.Opaque || u.BlendAlsoRequired) { hasAlphaReq = true; break; }
                    if (hasAlphaReq) ok = RunAlphaMetrics(a, b, mask, w, h, node, th);
                }
            }
            else if (role == TexRole.Mask)
            {
                var res = new NativeArray<float>(1, Allocator.TempJob);
                try
                {
                    byte usedCh = 0xF;
                    foreach (var u in node.Usages)
                        if (u.UsedChannels != 0) { usedCh = u.UsedChannels; break; }
                    var job = new GrayRmseJob
                    {
                        A = a, B = b, Mask = mask, W = w, H = h,
                        UsedChannels = usedCh, LinearSource = !node.Srgb, Result = res,
                    };
                    job.Schedule().Complete();
                    if (res[0] > th.grayRmseMax) ok = false;
                }
                finally { res.Dispose(); }
            }
            else
            {
                // linear color / 线性颜色贴图:SSIM + RGB RMSE
                if (!RunSsim(a, b, mask, w, h, th.msSsimMin)) ok = false;
                if (ok)
                {
                    var res = new NativeArray<float>(1, Allocator.TempJob);
                    try
                    {
                        var job = new GrayRmseJob
                        {
                            A = a, B = b, Mask = mask, W = w, H = h,
                            UsedChannels = 0x7, LinearSource = true, Result = res,
                        };
                        job.Schedule().Complete();
                        if (res[0] > Mathf.Max(th.grayRmseMax, th.deltaEMax * 0.01f)) ok = false;
                    }
                    finally { res.Dispose(); }
                }
            }
            return ok;
        }

        private bool RunAlphaMetrics(NativeArray<Color32> a, NativeArray<Color32> b, NativeArray<byte> mask,
            int w, int h, TextureNode node, QualityThresholds th)
        {
            var cutoffs = new List<float>();
            bool blend = false;
            foreach (var u in node.Usages)
            {
                if (u.Alpha == AlphaMode.Cutout)
                {
                    if (u.MultiCutoffs != null)
                    {
                        foreach (var c in u.MultiCutoffs) if (!cutoffs.Contains(c)) cutoffs.Add(c);
                    }
                    else if (!cutoffs.Contains(u.Cutoff)) cutoffs.Add(u.Cutoff);
                }
                if (u.Alpha == AlphaMode.Blend || u.BlendAlsoRequired) blend = true;
            }
            if (cutoffs.Count == 0 && !blend) return true;

            var res = new NativeArray<float>(2, Allocator.TempJob);
            var cutArr = new NativeArray<float>(Mathf.Max(1, cutoffs.Count), Allocator.TempJob);
            try
            {
                for (int i = 0; i < cutoffs.Count; i++) cutArr[i] = cutoffs[i];
                if (cutArr.Length == 0) cutArr[0] = 0.5f;
                bool evalIoU = cutoffs.Count > 0;
                var job = new AlphaMetricsJob
                {
                    A = a, B = b, Mask = mask, W = w, H = h,
                    Cutoffs = cutArr,
                    EvaluateIoU = evalIoU,
                    EvaluateRmse = blend,
                    Result = res,
                };
                job.Schedule().Complete();
                if (evalIoU && res[0] < th.alphaIoUMin) return false;
                if (blend && res[1] > th.alphaRmseMax) return false;
                return true;
            }
            finally
            {
                res.Dispose();
                cutArr.Dispose();
            }
        }

        private bool RunSsim(NativeArray<Color32> a, NativeArray<Color32> b, NativeArray<byte> mask,
            int w, int h, float threshold)
        {
            var res = new NativeArray<float>(1, Allocator.TempJob);
            var kernel = new NativeArray<float>(GaussianKernel, Allocator.TempJob);
            try
            {
                var job = new MsSsimJob { A = a, B = b, Mask = mask, W = w, H = h, Kernel = kernel, Result = res };
                job.Schedule().Complete();
                if (res[0] >= float.MaxValue) return true; // metric ignored (small island) / 忽略
                return res[0] >= threshold;
            }
            finally
            {
                res.Dispose();
                kernel.Dispose();
            }
        }

        private bool EvaluateNormal(GpuReadback rb, int bx, int by, int bw, int bh, int dstW, int dstH,
            NativeArray<byte> coverage, QualityThresholds th)
        {
            var regionA = ExtractRegion(rb, bx, by, bw, bh);
            var vecA = new NativeArray<float3>(bw * bh, Allocator.TempJob);
            var smallV = new NativeArray<float3>(dstW * dstH, Allocator.TempJob);
            var reconV = new NativeArray<float3>(bw * bh, Allocator.TempJob);
            try
            {
                var dec = new DecodeNormalsJob { Source = regionA, Count = bw * bh, Normals = vecA };
                dec.Schedule().Complete();

                var vd = new VectorDownsampleJob
                {
                    Source = vecA, SrcW = bw, SrcH = bh, Coverage = coverage,
                    DstW = dstW, DstH = dstH, Target = smallV,
                };
                vd.Schedule().Complete();

                var vu = new VectorUpsampleJob
                {
                    Small = smallV, SmallW = dstW, SmallH = dstH,
                    DstW = bw, DstH = bh, Dst = reconV,
                };
                vu.Schedule().Complete();

                var res = new NativeArray<float>(2, Allocator.TempJob);
                try
                {
                    var job = new NormalAngleJob { A = vecA, B = reconV, Mask = coverage, W = bw, H = bh, Result = res };
                    job.Schedule().Complete();
                    if (res[0] >= float.MaxValue) return true;
                    return res[0] <= th.normalAngleMeanMax && res[1] <= th.normalAngleP95Max;
                }
                finally { res.Dispose(); }
            }
            finally
            {
                regionA.Dispose();
                vecA.Dispose();
                smallV.Dispose();
                reconV.Dispose();
            }
        }

        // ------------------------------------------------------------------ //
        // Helpers / 辅助
        // ------------------------------------------------------------------ //
        private NativeArray<Color32> ExtractRegion(GpuReadback rb, int bx, int by, int w, int h)
        {
            var dst = new NativeArray<Color32>(w * h, Allocator.TempJob);
            var src = rb.Pixels;
            for (int y = 0; y < h; y++)
            {
                int srcRow = (by + y) * rb.Width + bx;
                int dstRow = y * w;
                for (int x = 0; x < w; x++) dst[dstRow + x] = src[srcRow + x];
            }
            return dst;
        }

        private IslandRasterMask GetPixelCoverage(IslandRef iref, UvIsland island, IslandSetData set, int texW, int texH)
        {
            if (island.PixelMask != null) return island.PixelMask;
            var mask = IslandRasterizer.RasterizePixels(set.NormalizedUvs, island.Triangles, island.UvBounds, texW, texH, 1);
            island.PixelMask = mask;
            return mask;
        }

        private bool IsPureColor(IslandRef iref, List<TextureNode> textures, int texW, int texH)
        {
            var set = _d.IslandSets[iref.SetId];
            var island = set.Islands[iref.IslandId];
            foreach (var node in textures)
            {
                var rb = ATOGpu.Instance.Readback(node.Tex);
                var cov = GetPixelCoverage(iref, island, set, node.Tex.width, node.Tex.height);
                var res = new NativeArray<int>(5, Allocator.TempJob);
                var region = new NativeArray<Color32>(cov.W * cov.H, Allocator.TempJob);
                try
                {
                    // cov covers the bbox at 1px cells / 覆盖为 1px 格
                    int bx = cov.OriginX, by = cov.OriginY;
                    for (int y = 0; y < cov.H; y++)
                    for (int x = 0; x < cov.W; x++)
                        region[y * cov.W + x] = rb.Pixels[(by + y) * rb.Width + bx + x];

                    var covNative = new NativeArray<byte>(cov.Bytes, Allocator.TempJob);
                    try
                    {
                        var job = new PureColorJob { Source = region, Mask = covNative, W = cov.W, H = cov.H, Result = res };
                        job.Schedule().Complete();
                        if (res[0] == 0) return false;
                    }
                    finally { covNative.Dispose(); }
                }
                finally
                {
                    res.Dispose();
                    region.Dispose();
                }
            }
            return true;
        }

        // ------------------------------------------------------------------ //
        // Whole-texture scaling (no-atlas mode) / 整图缩放(不生成图集模式)
        // ------------------------------------------------------------------ //
        private void FillWholeTexScales(bool onlyFallback)
        {
            // Island decisions already computed by ScaleIslands (always executed). / 岛决策已由 ScaleIslands 计算(始终执行)。
            int done = 0;
            var all = _d.TextureNodes.Values
                .Where(n => !_d.WhitelistedTextures.Contains(n.Tex))
                .Where(n => !onlyFallback || !n.Atlased)
                .ToList();
            foreach (var node in all)
            {
                Tick($"ATO: scaling textures ({done}/{all.Count})", 0.05f + 0.4f * done / Mathf.Max(1, all.Count));
                done++;
                float s = 1f;
                foreach (var iref in node.IslandRefs)
                {
                    IslandScaleDecision dec;
                    if (!TryGetDecision(iref, out dec)) continue;
                    // convert reference-space decision to this texture's own scale / 将基准空间决策换算为本贴图缩放
                    float ex = Mathf.Min(1f, dec.Sx * dec.RefW / Mathf.Max(1, node.Tex.width));
                    float ey = Mathf.Min(1f, dec.Sy * dec.RefH / Mathf.Max(1, node.Tex.height));
                    float m = Mathf.Min(ex, ey);
                    if (m < s) s = m;
                }
                _wholeTexScale[node.Tex] = s;
            }
            ATOLog.Info($"whole-texture scaling decided for {all.Count} textures (fallback-only: {onlyFallback})");
        }

        private bool TryGetDecision(IslandRef iref, out IslandScaleDecision dec)
        {
            foreach (var g in _d.UvGroups)
                foreach (var d in g.ScaleDecisions)
                    if (d.SetId == iref.SetId && d.IslandId == iref.IslandId)
                    {
                        dec = d;
                        return true;
                    }
            dec = new IslandScaleDecision();
            return false;
        }

        /// <summary>Mark nodes fully placed into atlases. / 标记已完整放入图集的贴图节点。</summary>
        private void MarkAtlasedNodes()
        {
            foreach (var node in _d.TextureNodes.Values)
            {
                if (node.NoAtlas || node.IslandRefs.Count == 0) { node.Atlased = false; continue; }
                bool all = true;
                foreach (var iref in node.IslandRefs)
                    if (!_placementIndex.Contains(iref.Key)) { all = false; break; }
                node.Atlased = all;
            }
        }

        private readonly HashSet<long> _placementIndex = new HashSet<long>();

        private readonly Dictionary<Texture2D, float> _wholeTexScale = new Dictionary<Texture2D, float>();
    }
}
