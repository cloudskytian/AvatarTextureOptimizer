// Stage3_Quality — per-island scale decision by perceptual quality / 按感知质量决定每岛缩放
// Flow per island×texture: crop → solid shortcut → uniform binary search → per-axis refinement →
// wood-barrel unify across the UV group. Evaluation = shrink + bilinear upsample back, compare with
// original in linear space. Lossless tier short-circuits to original copy; solid islands shrink to
// min(4, short side).<br>
// 逐岛×贴图：裁剪 → 纯色短路 → 均匀二分 → 双轴细化 → UV组木桶统一。评估=缩小后双线性放大回原尺寸对比。
// 无损挡位直接原样拷贝；纯色岛缩到 min(4,短边)。
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    internal static class Stage3_Quality
    {
        internal static void Run(BuildContext ctx, ATOPipeContext pipe, StageProgress progress)
        {
            var settings = pipe.settings;
            int total = pipe.islands.Count, done = 0;

            foreach (var g in pipe.groups)
            {
                foreach (var isl in g.islands)
                {
                    done++;
                    if ((done & 7) == 0) pipe.CancelCheck(progress, ATOL10n.T("ato.stage.quality"), (float)done / Mathf.Max(1, total));
                    DecideIsland(pipe, g, isl, settings);
                }
                // non-atlas bookkeeping: max per-axis scale per texture / 非图集记账：各贴图按轴取最大缩放
                foreach (var info in g.textures)
                {
                    if (info.whitelisted) continue;
                    var maxT = Vector2.zero;
                    foreach (var isl in g.islands)
                    {
                        var t = TargetFor(pipe, g, isl, info);
                        if (t.x <= 0 || t.y <= 0) continue;
                        var bbox = BboxPx(isl, info);
                        float sx = Mathf.Min(1f, t.x / (float)Mathf.Max(1, bbox.width));
                        float sy = Mathf.Min(1f, t.y / (float)Mathf.Max(1, bbox.height));
                        if (isl.unifiedSize.x <= 0) { sx = sy = 1f; }
                        maxT.x = Mathf.Max(maxT.x, sx); maxT.y = Mathf.Max(maxT.y, sy);
                    }
                    if (maxT.x <= 0) maxT = Vector2.one;
                    pipe.wholeTextureScale[info] = maxT;
                }
            }
            ATOEvents.Raise("quality", pipe, ctx.AvatarRootObject);
            ATOHookRegistry.Notify("quality", pipe);
        }

        internal static RectInt BboxPx(Island isl, TextureInfo info)
        {
            int x0 = Mathf.FloorToInt(isl.nMin.x * info.width);
            int y0 = Mathf.FloorToInt(isl.nMin.y * info.height);
            int x1 = Mathf.CeilToInt(isl.nMax.x * info.width);
            int y1 = Mathf.CeilToInt(isl.nMax.y * info.height);
            x1 = Mathf.Clamp(x1, x0 + 1, info.width);
            y1 = Mathf.Clamp(y1, y0 + 1, info.height);
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>Per-texture target px size (computed at DecideIsland and cached by caller path). / 每贴图目标像素尺寸。</summary>
        private static Vector2Int TargetFor(ATOPipeContext pipe, PackingGroup g, Island isl, TextureInfo info)
        {
            // recompute cheap: unified stored; per-texture recorded in side map / 从附加记录读取
            if (isl.perTextureTarget != null && isl.perTextureTarget.TryGetValue(info, out var t)) return t;
            return Vector2Int.zero;
        }

        private static void DecideIsland(ATOPipeContext pipe, PackingGroup g, Island isl, ATOSettingsSnap settings)
        {
            var bboxMax = Vector2Int.zero;
            isl.perTextureTarget ??= new Dictionary<TextureInfo, Vector2Int>();

            // largest original bbox across textures (upper bound, no upscale) / 组内最大原包围盒（上界）
            foreach (var info in CandidateTextures(pipe, g, isl))
            {
                var b = BboxPx(isl, info);
                bboxMax.x = Mathf.Max(bboxMax.x, b.width);
                bboxMax.y = Mathf.Max(bboxMax.y, b.height);
            }
            if (bboxMax.x <= 0) { isl.unifiedSize = new Vector2Int(1, 1); return; }

            if (settings.Lossless)
            {
                // quality == 1 → skip scaling entirely (incl. solid), copy as-is / 质量为1：完全跳过缩放，原样拷贝
                isl.skipScale = true;
                isl.unifiedSize = bboxMax;
                foreach (var info in CandidateTextures(pipe, g, isl))
                    isl.perTextureTarget[info] = new Vector2Int(BboxPx(isl, info).width, BboxPx(isl, info).height);
                return;
            }

            foreach (var info in CandidateTextures(pipe, g, isl))
            {
                var bbox = BboxPx(isl, info);
                // states: alpha semantics of every material referencing this texture here / 取质量最严的所有引用状态
                var states = AlphaStatesOf(g, info);
                var scale = SearchScale(pipe, isl, info, bbox, states, settings);

                // density baseline / 像素密度基准
                float area = isl.worldAreaMax;
                if (area > 1e-8f)
                {
                    float cur = Mathf.Sqrt((float)bbox.width * bbox.height / area);
                    if (cur > 1e-6f)
                    {
                        float lo = settings.minDensity / cur, hi = Mathf.Min(1f, settings.maxDensity / cur);
                        scale.x = Mathf.Clamp(scale.x, Mathf.Min(lo, 1f), Mathf.Max(hi, Mathf.Min(lo, 1f)));
                        scale.y = Mathf.Clamp(scale.y, Mathf.Min(lo, 1f), Mathf.Max(hi, Mathf.Min(lo, 1f)));
                        scale = Vector2.Min(scale, Vector2.one);
                    }
                }
                int tw = Mathf.Clamp(Mathf.CeilToInt(bbox.width * scale.x), 1, bbox.width);
                int th = Mathf.Clamp(Mathf.CeilToInt(bbox.height * scale.y), 1, bbox.height);
                isl.perTextureTarget[info] = new Vector2Int(tw, th);
            }

            // wood-barrel unify / 木桶效应统一（轴最大值）
            var uni = Vector2Int.one;
            foreach (var kv in isl.perTextureTarget)
            {
                uni.x = Mathf.Max(uni.x, kv.Value.x);
                uni.y = Mathf.Max(uni.y, kv.Value.y);
            }
            uni.x = Mathf.Min(uni.x, bboxMax.x);
            uni.y = Mathf.Min(uni.y, bboxMax.y);
            isl.unifiedSize = uni;
            var bbRef = bboxMax;
            isl.reqScale = new Vector2(uni.x / (float)Mathf.Max(1, bbRef.x), uni.y / (float)Mathf.Max(1, bbRef.y));
        }

        private static IEnumerable<TextureInfo> CandidateTextures(ATOPipeContext pipe, PackingGroup g, Island isl)
        {
            foreach (var info in g.textures)
            {
                if (info.whitelisted) continue; // whitelisted textures never get scaled / 白名单贴图绝不缩放
                yield return info;
            }
        }

        private static List<(AlphaMode mode, float cutoff)> AlphaStatesOf(PackingGroup g, TextureInfo info)
        {
            var list = new List<(AlphaMode, float)>();
            foreach (var r in g.refs)
            {
                if (r.cls != TexClass.Albedo || !r.textures.Contains(info.source)) continue;
                var st = (r.alphaMode, r.cutoff);
                if (!list.Contains(st)) list.Add(st);
            }
            if (list.Count == 0) list.Add((AlphaMode.Opaque, 0.5f));
            return list;
        }

        // ---------------------------------------------------------------- scale search
        private static Vector2 SearchScale(ATOPipeContext pipe, Island isl, TextureInfo info, RectInt bbox,
            List<(AlphaMode mode, float cutoff)> states, ATOSettingsSnap settings)
        {
            bool solid = DetectSolid(isl, info, bbox, out _);
            if (solid)
            {
                // solid shortcut: min(4, short side) / 纯色短路：min(4,短边)
                int s = Mathf.Min(4, Mathf.Min(bbox.width, bbox.height));
                isl.solidColor = true;
                return new Vector2(s / (float)bbox.width, s / (float)bbox.height);
            }

            // uniform binary search: min passing scale / 均匀二分：最小达标缩放
            float lo = 0.02f, hi = 1f, best = 1f;
            for (int i = 0; i < 8; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (PassAll(pipe, isl, info, bbox, states, new Vector2(mid, mid), settings)) { best = mid; hi = mid; }
                else lo = mid;
            }
            // per-axis refinement (anisotropy) / 双轴独立二分细化（各向异性）
            float bx = best, by = best;
            lo = 0.02f; hi = bx;
            for (int i = 0; i < 6; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (PassAll(pipe, isl, info, bbox, states, new Vector2(mid, by), settings)) { bx = mid; hi = mid; }
                else lo = mid;
            }
            lo = 0.02f; hi = by;
            for (int i = 0; i < 6; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (PassAll(pipe, isl, info, bbox, states, new Vector2(bx, mid), settings)) { by = mid; hi = mid; }
                else lo = mid;
            }
            return new Vector2(bx, by);
        }

        // ---------------------------------------------------------------- evaluation
        /// <summary>Shrink by (sx,sy), upsample back, all metrics must pass. / 缩小→双线性放回→全部指标达标。</summary>
        private static bool PassAll(ATOPipeContext pipe, Island isl, TextureInfo info, RectInt bbox,
            List<(AlphaMode mode, float cutoff)> states, Vector2 scale, ATOSettingsSnap settings)
        {
            int dw = Mathf.Max(1, Mathf.RoundToInt(bbox.width * scale.x));
            int dh = Mathf.Max(1, Mathf.RoundToInt(bbox.height * scale.y));
            var th = settings.thresholds;

            var lin = ImageCache.GetLinear(info.source, info.sRGB, out int tw, out int th0);
            if (lin == null) return false;
            int n = bbox.width * bbox.height;
            var orig = new NativeArray<float>(n * 4, Allocator.TempJob);
            NativeArray<float> small = default, up = default;
            try
            {
                CropTo(lin, tw, th0, bbox, orig, bbox.width, bbox.height);

                bool premult = false;
                foreach (var st in states) if (st.mode != AlphaMode.Opaque) { premult = true; break; }
                if (premult)
                    new QualityJobs.PremultiplyJob { buf = orig, unpremultiply = false }.Schedule(n, 64).Complete();

                small = QualityJobs.Resample(orig, bbox.width, bbox.height, dw, dh, Allocator.TempJob);
                if (info.classes.Contains(TexClass.Normal))
                    new QualityJobs.RenormalizeJob { buf = small }.Schedule(dw * dh, 64).Complete();
                up = QualityJobs.Resample(small, dw, dh, bbox.width, bbox.height, Allocator.TempJob);
                if (info.classes.Contains(TexClass.Normal))
                    new QualityJobs.RenormalizeJob { buf = up }.Schedule(n, 64).Complete();

                // ---- per-class gates / 分类质量门 ----
                if (info.classes.Contains(TexClass.Albedo))
                {
                    int short0 = Mathf.Min(bbox.width, bbox.height);
                    if (short0 >= 11)
                    {
                        float ssim = short0 >= 176 ? MsSsim(orig, up, bbox.width, bbox.height) : Ssim(orig, up, bbox.width, bbox.height);
                        if (ssim < th.msSsimMin) return false;
                    }
                    float deP95 = DeltaEP95(orig, up, n);
                    if (deP95 > th.deltaEMaxP95) return false;

                    foreach (var st in states)
                    {
                        switch (st.mode)
                        {
                            case AlphaMode.Cutout:
                                float iou = CutoutIoU(orig, up, n, Mathf.Clamp01(st.cutoff));
                                if (iou < th.cutoutIouMin) return false;
                                break;
                            case AlphaMode.Blend:
                                float rmse = ChannelRmse(orig, up, n, 1 << 3);
                                if (rmse > th.alphaRmseMax) return false;
                                break;
                        }
                    }
                }
                else if (info.classes.Contains(TexClass.Normal))
                {
                    var ang = new NativeArray<float>(n, Allocator.TempJob);
                    new QualityJobs.NormalAngleJob { a = orig, b = up, outDeg = ang }.Schedule(n, 64).Complete();
                    float mean = QualityJobs.Mean(ang);
                    float p95 = QualityJobs.P95(ang, th.normalAngleP95Deg * 4);
                    ang.Dispose();
                    if (mean > th.normalAngleMeanDeg || p95 > th.normalAngleP95Deg) return false;
                }
                else // Mask
                {
                    int flags = MaskFlags(pipe, info);
                    float rmse = ChannelRmse(orig, up, n, flags);
                    if (rmse > th.maskRmseMax) return false;
                }
                return true;
            }
            finally
            {
                if (orig.IsCreated) orig.Dispose();
                if (small.IsCreated) small.Dispose();
                if (up.IsCreated) up.Dispose();
            }
        }

        private static int MaskFlags(ATOPipeContext pipe, TextureInfo info)
        {
            // referenced channels from any ref of this texture / 该贴图所有引用通道
            int flags = 0;
            foreach (var kv in pipe.slotRefs)
                foreach (var r in kv.Value)
                    if (r.cls == TexClass.Mask && r.textures.Contains(info.source)) flags |= r.maskChannelMask;
            return flags == 0 ? 0xF : flags;
        }

        // ---------------------------------------------------------------- pixel ops
        private static void CropTo(float[] full, int fullW, int fullH, RectInt r, NativeArray<float> dst, int dw, int dh)
        {
            for (int y = 0; y < dh; y++)
            {
                int srcRow = (Mathf.Clamp(r.y + y, 0, fullH - 1) * fullW) * 4;
                int dstRow = y * dw * 4;
                for (int x = 0; x < dw; x++)
                {
                    int sx = Mathf.Clamp(r.x + x, 0, fullW - 1);
                    int si = srcRow + sx * 4, di = dstRow + x * 4;
                    dst[di] = full[si]; dst[di + 1] = full[si + 1]; dst[di + 2] = full[si + 2]; dst[di + 3] = full[si + 3];
                }
            }
        }

        private static bool DetectSolid(Island isl, TextureInfo info, RectInt bbox, out bool solid)
        {
            solid = false;
            var lin = ImageCache.GetLinear(info.source, info.sRGB, out int tw, out int th);
            if (lin == null) return false;
            float mn0 = 1, mn1 = 1, mn2 = 1, mn3 = 1, mx0 = 0, mx1 = 0, mx2 = 0, mx3 = 0;
            for (int y = 0; y < bbox.height; y++)
            {
                int row = (Mathf.Clamp(bbox.y + y, 0, th - 1) * tw) * 4;
                for (int x = 0; x < bbox.width; x++)
                {
                    int i = row + Mathf.Clamp(bbox.x + x, 0, tw - 1) * 4;
                    mn0 = Mathf.Min(mn0, lin[i]); mx0 = Mathf.Max(mx0, lin[i]);
                    mn1 = Mathf.Min(mn1, lin[i + 1]); mx1 = Mathf.Max(mx1, lin[i + 1]);
                    mn2 = Mathf.Min(mn2, lin[i + 2]); mx2 = Mathf.Max(mx2, lin[i + 2]);
                    mn3 = Mathf.Min(mn3, lin[i + 3]); mx3 = Mathf.Max(mx3, lin[i + 3]);
                }
            }
            const float eps = 1f / 255f;
            return solid = (mx0 - mn0 <= eps && mx1 - mn1 <= eps && mx2 - mn2 <= eps && mx3 - mn3 <= eps);
        }

        // ---------------------------------------------------------------- metrics implementations
        private static float DeltaEP95(NativeArray<float> a, NativeArray<float> b, int n)
        {
            var de = new NativeArray<float>(n, Allocator.TempJob);
            new QualityJobs.DeltaEJob { a = a, b = b, outDE = de }.Schedule(n, 64).Complete();
            float p = QualityJobs.P95(de, 20f);
            de.Dispose();
            return p;
        }

        private static float ChannelRmse(NativeArray<float> a, NativeArray<float> b, int n, int channelFlags)
        {
            var err = new NativeArray<float>(n, Allocator.TempJob);
            new QualityJobs.SqErrorJob { a = a, b = b, channelFlags = channelFlags, outErr = err }.Schedule(n, 64).Complete();
            double sum = 0; int channels = 0;
            for (int c = 0; c < 4; c++) if ((channelFlags & (1 << c)) != 0) channels++;
            for (int i = 0; i < n; i++) sum += err[i];
            err.Dispose();
            return QualityJobs.MeanSqToRmse(sum, (long)n * Mathf.Max(1, channels));
        }

        private static float CutoutIoU(NativeArray<float> a, NativeArray<float> b, int n, float cutoff)
        {
            var masks = new NativeArray<byte>(n, Allocator.TempJob);
            new QualityJobs.CutoutMaskJob { a = a, b = b, cutoff = cutoff, masks = masks }.Schedule(n, 64).Complete();
            float v = QualityJobs.IoU(masks);
            masks.Dispose();
            return v;
        }

        private static float Ssim(NativeArray<float> a, NativeArray<float> b, int w, int h)
        {
            int n = w * h;
            var la = new NativeArray<float>(n, Allocator.TempJob);
            var lb = new NativeArray<float>(n, Allocator.TempJob);
            var map = new NativeArray<float>(n, Allocator.TempJob);
            new QualityJobs.LuminanceJob { rgba = a, lum = la }.Schedule(n, 64).Complete();
            new QualityJobs.LuminanceJob { rgba = b, lum = lb }.Schedule(n, 64).Complete();
            new QualityJobs.SsimJob { lumA = la, lumB = lb, w = w, h = h, csOnly = false, outMap = map }.Schedule(n, 32).Complete();
            float v = QualityJobs.Mean(map);
            la.Dispose(); lb.Dispose(); map.Dispose();
            return v;
        }

        private static float MsSsim(NativeArray<float> a, NativeArray<float> b, int w, int h)
        {
            // standard 5-level MS-SSIM; weights per Wang et al. / 标准5层 MS-SSIM（Wang 等权重）
            float[] weights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
            int n = w * h;
            var la = new NativeArray<float>(n, Allocator.TempJob);
            var lb = new NativeArray<float>(n, Allocator.TempJob);
            new QualityJobs.LuminanceJob { rgba = a, lum = la }.Schedule(n, 64).Complete();
            new QualityJobs.LuminanceJob { rgba = b, lum = lb }.Schedule(n, 64).Complete();

            double score = 1.0;
            int cw = w, ch = h;
            var ca = la; var cb = lb;
            var keepA = new List<NativeArray<float>> { ca };
            var keepB = new List<NativeArray<float>> { cb };
            for (int level = 0; level < 5; level++)
            {
                int cn = cw * ch;
                var map = new NativeArray<float>(cn, Allocator.TempJob);
                bool last = level == 4;
                new QualityJobs.SsimJob { lumA = ca, lumB = cb, w = cw, h = ch, csOnly = !last, outMap = map }.Schedule(cn, 32).Complete();
                float m = QualityJobs.Mean(map);
                map.Dispose();
                score *= Math.Pow(Mathf.Clamp(m, 1e-6f, 1f), weights[level]);
                if (last) break;
                int nw = Mathf.Max(1, (cw + 1) / 2), nh = Mathf.Max(1, (ch + 1) / 2);
                if (nw * nh < 16) // pyramid too small: fold remaining weight into current level / 金字塔过小：提前收敛
                {
                    float remain = 0f; for (int l = level + 1; l < 5; l++) remain += weights[l];
                    score *= Math.Pow(Mathf.Clamp(m, 1e-6f, 1f), remain);
                    break;
                }
                var na = new NativeArray<float>(nw * nh, Allocator.TempJob);
                var nb = new NativeArray<float>(nw * nh, Allocator.TempJob);
                new QualityJobs.HalfJob { src = ca, srcW = cw, srcH = ch, dst = na, dstW = nw, dstH = nh }.Schedule(nw * nh, 64).Complete();
                new QualityJobs.HalfJob { src = cb, srcW = cw, srcH = ch, dst = nb, dstW = nw, dstH = nh }.Schedule(nw * nh, 64).Complete();
                ca = na; cb = nb; cw = nw; ch = nh;
                keepA.Add(ca); keepB.Add(cb);
            }
            foreach (var x in keepA) if (x.IsCreated) x.Dispose();
            foreach (var x in keepB) if (x.IsCreated) x.Dispose();
            return (float)score;
        }
    }
}
