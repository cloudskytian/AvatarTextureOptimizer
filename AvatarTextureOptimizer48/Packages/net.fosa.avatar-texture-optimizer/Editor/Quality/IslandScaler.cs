// Island scaling: computes per-island target atlas sizes via quality binary search with
// density clamps and anisotropy refinement, then buckets by UV group (max size rule).
// / 岛缩放：通过质量二分搜索计算每个岛的图集目标尺寸，应用像素密度钳制与各向异性细化，
// 再按 UV 组木桶效应（取最大尺寸）确定最终尺寸。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using net.fosa.avatar_texture_optimizer.editor.analysis;
using net.fosa.avatar_texture_optimizer.editor.pipeline;
using net.fosa.avatar_texture_optimizer.runtime;

namespace net.fosa.avatar_texture_optimizer.editor.quality
{
    /// <summary>Effective quality thresholds. / 生效的质量阈值。</summary>
    public sealed class QualityBar
    {
        public float Ssim = 0.995f;
        public float DeltaE = 0.75f;
        public float Alpha = 0.005f;
        public float NormalAngle = 1.5f;
        public float GrayRms = 0.005f;
        public bool NearLossless;

        public static QualityBar FromSettings(AvatarTextureOptimizer.QualitySettings q)
        {
            var bar = new QualityBar();
            switch (q.preset)
            {
                case AvatarTextureOptimizer.QualityPreset.High:
                    bar.Ssim = 0.995f; bar.DeltaE = 0.75f; bar.Alpha = 0.005f; bar.NormalAngle = 1.5f; bar.GrayRms = 0.005f;
                    break;
                case AvatarTextureOptimizer.QualityPreset.Medium:
                    bar.Ssim = 0.985f; bar.DeltaE = 1.5f; bar.Alpha = 0.02f; bar.NormalAngle = 3f; bar.GrayRms = 0.02f;
                    break;
                case AvatarTextureOptimizer.QualityPreset.Low:
                    bar.Ssim = 0.95f; bar.DeltaE = 3f; bar.Alpha = 0.05f; bar.NormalAngle = 6f; bar.GrayRms = 0.05f;
                    break;
                case AvatarTextureOptimizer.QualityPreset.Custom:
                    bar.Ssim = q.custom.ssim; bar.DeltaE = q.custom.deltaE; bar.Alpha = q.custom.alpha;
                    bar.NormalAngle = q.custom.normalAngle; bar.GrayRms = q.custom.grayRms;
                    break;
            }
            bar.NearLossless = q.preset == AvatarTextureOptimizer.QualityPreset.Custom
                               && q.custom.ssim >= 0.999f && q.custom.deltaE <= 0.001f
                               && q.custom.alpha <= 0.0001f && q.custom.normalAngle <= 0.01f
                               && q.custom.grayRms <= 0.0001f;
            return bar;
        }
    }

    /// <summary>
    /// Computes final island sizes. / 计算岛的最终尺寸。
    /// </summary>
    public static class IslandScaler
    {
        // Cache of reference regions: (group, texture, island) -> linear premultiplied RGBA of the island bbox.
        // / 参考区域缓存：(组, 贴图, 岛) -> 岛包围盒的线性预乘 RGBA。
        private sealed class RegionCache
        {
            private readonly Dictionary<(TexRecord, Island), float[]> _map = new Dictionary<(TexRecord, Island), float[]>();
            private readonly Dictionary<TexRecord, byte[]> _bytes = new Dictionary<TexRecord, byte[]>();
            private readonly Dictionary<TexRecord, bool> _pure = new Dictionary<TexRecord, bool>();

            public byte[] Bytes(TexRecord record)
            {
                if (!_bytes.TryGetValue(record, out var b))
                {
                    var px = TextureReader.ReadPixels(record.Texture);
                    b = new byte[px.Length * 4];
                    for (int i = 0; i < px.Length; i++)
                    {
                        b[i * 4] = px[i].r; b[i * 4 + 1] = px[i].g; b[i * 4 + 2] = px[i].b; b[i * 4 + 3] = px[i].a;
                    }
                    _bytes[record] = b;
                }
                return b;
            }

            public bool IsPureColor(TexRecord record)
            {
                if (!_pure.TryGetValue(record, out var p))
                {
                    var b = Bytes(record);
                    byte r = b[0], g = b[1], bl = b[2], a = b[3];
                    p = true;
                    for (int i = 4; i < b.Length; i += 4)
                    {
                        if (b[i] != r || b[i + 1] != g || b[i + 2] != bl || b[i + 3] != a) { p = false; break; }
                    }
                    _pure[record] = p;
                }
                return p;
            }

            public float[] Region(TexRecord record, Island island, out int rw, out int rh)
            {
                if (_map.TryGetValue((record, island), out var cached))
                {
                    rw = (int)((island.Max.x - island.Min.x) * record.Width);
                    rh = (int)((island.Max.y - island.Min.y) * record.Height);
                    return cached;
                }
                var b = Bytes(record);
                int x0 = Mathf.Clamp(Mathf.FloorToInt(island.Min.x * record.Width), 0, record.Width - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(island.Min.y * record.Height), 0, record.Height - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt(island.Max.x * record.Width), 1, record.Width);
                int y1 = Mathf.Clamp(Mathf.CeilToInt(island.Max.y * record.Height), 1, record.Height);
                rw = Mathf.Max(1, x1 - x0);
                rh = Mathf.Max(1, y1 - y0);
                var region = TextureOps.RegionRgbaLinear(b, record.Width, record.Height, x0, y0, rw, rh);
                if (_map.Count < 8192) _map[(record, island)] = region;
                return region;
            }
        }

        /// <summary>
        /// Compute scales for all groups. / 为所有组计算缩放。
        /// </summary>
        public static void ComputeScales(AnalysisResult analysis, AvatarTextureOptimizer component, ProgressScope progress)
        {
            var bar = QualityBar.FromSettings(component.quality);
            var cache = new RegionCache();

            int totalIslands = 0;
            foreach (var g in analysis.UvGroups) totalIslands += g.Islands.Count;
            int done = 0;

            foreach (var group in analysis.UvGroups)
            {
                // Group max texture dimension / 组内最大贴图尺寸
                int groupMaxDim = 1;
                foreach (var gt in group.Textures)
                {
                    groupMaxDim = Mathf.Max(groupMaxDim, Mathf.Max(gt.Record.Width, gt.Record.Height));
                }

                bool nearLossless = false;
                bool anyPure = false;
                foreach (var gt in group.Textures)
                {
                    if (bar.NearLossless) { nearLossless = true; break; }
                    bool pure = cache.IsPureColor(gt.Record);
                    gt.PureColor = pure;
                    if (pure) anyPure = true;
                    if (gt.Role == TextureRole.MainColor && pure) anyPure = true;
                }
                if (bar.NearLossless) nearLossless = true;
                group.AllPureColor = anyPure && group.Textures.Count > 0;

                // Per-island computation / 逐岛计算
                Parallel.ForEach(group.Islands, island =>
                {
                    try
                    {
                        ComputeIsland(group, island, component, bar, cache, groupMaxDim, nearLossless);
                    }
                    catch (Exception e)
                    {
                        lock (AtoLogLock())
                        {
                            AtoLog.Warn("Island scaling failed for island " + island.Id + ": " + e.Message);
                        }
                        island.GroupScale = 1f;
                        island.AtlasW = (int)island.OrigLongSidePx;
                        island.AtlasH = (int)island.OrigShortSidePx;
                    }
                    System.Threading.Interlocked.Increment(ref done);
                    if (done % 256 == 0 && progress != null)
                    {
                        progress.Report("Scaling islands / 缩放 UV 岛",
                            done + " / " + totalIslands, 0.35f + 0.3f * done / (float)Mathf.Max(1, totalIslands));
                    }
                });

                // Per-texture required scales recorded on GroupTexture / 记录每张贴图的理想缩放
                foreach (var gt in group.Textures) gt.RequiredScale = islandRequiredScaleForTexture(group, gt);
            }
        }

        private static object AtoLogLock() => _logLock;
        private static readonly object _logLock = new object();

        private static float islandRequiredScaleForTexture(UVGroup group, GroupTexture gt)
        {
            // approximate: average of per-island group scale / 用组缩放的近似值
            float sum = 0; int n = 0;
            foreach (var iso in group.Islands)
            {
                sum += iso.GroupScale; n++;
            }
            return n == 0 ? 1f : sum / n;
        }

        private static void ComputeIsland(UVGroup group, Island island, AvatarTextureOptimizer component,
            QualityBar bar, RegionCache cache, int groupMaxDim, bool nearLossless)
        {
            float bboxW = island.Max.x - island.Min.x;
            float bboxH = island.Max.y - island.Min.y;
            island.OrigLongSidePx = bboxW >= bboxH ? bboxW * groupMaxDim : bboxH * groupMaxDim;
            island.OrigShortSidePx = bboxW >= bboxH ? bboxH * groupMaxDim : bboxW * groupMaxDim;

            // Density clamps (px) / 像素密度钳制（像素）
            float minTex = component.quality.minPixelsPerMeter * island.WorldSize;
            float maxTex = component.quality.maxPixelsPerMeter * island.WorldSize;
            float densityMin = Mathf.Min(1f, Mathf.Max(1f, minTex) / Mathf.Max(1f, island.OrigLongSidePx));
            float densityMax = Mathf.Min(1f, maxTex / Mathf.Max(1f, island.OrigLongSidePx));
            island.DensityScaleMin = densityMin;
            island.DensityScaleMax = Mathf.Max(densityMin, densityMax);

            // Near-lossless: keep original size, no resampling / 近无损：保持原尺寸
            if (nearLossless)
            {
                island.GroupScale = 1f;
                island.AtlasW = Mathf.Max(1, Mathf.RoundToInt(island.OrigLongSidePx));
                island.AtlasH = Mathf.Max(1, Mathf.RoundToInt(island.OrigShortSidePx));
                foreach (var gt in group.Textures) gt.SkipScaling = true;
                return;
            }

            // All pure color: shortcut to min(4, short side) / 全部纯色：直接缩到最小
            bool allPure = group.AllPureColor;
            if (allPure)
            {
                float minSide = Mathf.Min(4f, island.OrigShortSidePx);
                island.GroupScale = Mathf.Max(0.01f, minSide / Mathf.Max(1f, island.OrigLongSidePx));
                island.AtlasW = Mathf.Max(1, Mathf.RoundToInt(island.OrigLongSidePx * island.GroupScale));
                island.AtlasH = Mathf.Max(1, Mathf.RoundToInt(island.OrigShortSidePx * island.GroupScale));
                return;
            }

            // Per-texture uniform binary search / 逐贴图均匀二分
            float maxReqW = 0, maxReqH = 0, maxNaturalW = 0, maxNaturalH = 0;
            foreach (var gt in group.Textures)
            {
                if (cache.IsPureColor(gt.Record)) continue;   // pure color follows group size / 纯色跟随组尺寸
                var refRgba = cache.Region(gt.Record, island, out int rw, out int rh);
                if (rw <= 1 || rh <= 1) continue;
                float natW = rw, natH = rh;
                maxNaturalW = Mathf.Max(maxNaturalW, natW);
                maxNaturalH = Mathf.Max(maxNaturalH, natH);

                float lo = densityMin, hi = 1f;
                for (int it = 0; it < 7; it++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (QualityEvaluator.Passes(refRgba, rw, rh, Mathf.Max(1, Mathf.RoundToInt(natW * mid)),
                               Mathf.Max(1, Mathf.RoundToInt(natH * mid)), gt.Record, gt.Role, bar))
                    {
                        hi = mid;
                    }
                    else lo = mid;
                }
                maxReqW = Mathf.Max(maxReqW, natW * hi);
                maxReqH = Mathf.Max(maxReqH, natH * hi);
            }

            // If all textures pure color / 若所有贴图纯色
            if (maxReqW <= 0)
            {
                float minSide = Mathf.Min(4f, island.OrigShortSidePx);
                island.GroupScale = Mathf.Max(0.01f, minSide / Mathf.Max(1f, island.OrigLongSidePx));
                island.AtlasW = Mathf.Max(1, Mathf.RoundToInt(island.OrigLongSidePx * island.GroupScale));
                island.AtlasH = Mathf.Max(1, Mathf.RoundToInt(island.OrigShortSidePx * island.GroupScale));
                return;
            }

            // Bucket: max size across textures, capped by max original and density / 木桶：取最大值，受原尺寸与密度钳制
            float rectW = Mathf.Clamp(maxReqW, island.OrigLongSidePx * densityMin, island.OrigLongSidePx * densityMax);
            float rectH = Mathf.Clamp(maxReqH, island.OrigShortSidePx * densityMin, island.OrigShortSidePx * densityMax);
            // cap by max natural size in group / 不超过组内最大原尺寸
            rectW = Mathf.Min(rectW, Mathf.Max(1f, maxNaturalW));
            rectH = Mathf.Min(rectH, Mathf.Max(1f, maxNaturalH));

            // Axis refinement: x then y / 双轴细化：先 x 后 y
            rectW = RefineAxis(group, island, cache, bar, rectW, rectH, true, densityMin, maxNaturalW);
            rectH = RefineAxis(group, island, cache, bar, rectW, rectH, false, densityMin, maxNaturalH);

            island.AtlasW = Mathf.Max(1, Mathf.RoundToInt(rectW));
            island.AtlasH = Mathf.Max(1, Mathf.RoundToInt(rectH));
            island.GroupScale = Mathf.Max(0.01f, Mathf.Max(rectW, rectH) / Mathf.Max(1f, island.OrigLongSidePx));
            // Source rect = full bbox (sampling scaled in baking) / 源矩形 = 完整包围盒（烘焙时缩放采样）
            island.ScaledRect = new Rect(island.Min.x, island.Min.y, bboxW, bboxH);
        }

        /// <summary>Binary-search one axis (x or y) for the smallest size that still passes. / 单轴二分求仍达标的最小尺寸。</summary>
        private static float RefineAxis(UVGroup group, Island island, RegionCache cache, QualityBar bar,
            float w, float h, bool isX, float densityMin, float maxNatural)
        {
            float lo = isX ? island.OrigLongSidePx * densityMin : island.OrigShortSidePx * densityMin;
            float hi = isX ? w : h;
            if (hi - lo < 1f) return hi;
            for (int it = 0; it < 6; it++)
            {
                float mid = (lo + hi) * 0.5f;
                if (GroupPasses(group, island, cache, bar, isX ? mid : w, isX ? h : mid))
                {
                    hi = mid;
                }
                else lo = mid;
            }
            return Mathf.Min(hi, maxNatural);
        }

        /// <summary>Evaluate the group at a given rect size: all non-pure textures must pass. / 在给定矩形尺寸下评估整个组：所有非纯色贴图必须通过。</summary>
        private static bool GroupPasses(UVGroup group, Island island, RegionCache cache, QualityBar bar,
            float w, float h)
        {
            int tw = Mathf.Max(1, Mathf.RoundToInt(w));
            int th = Mathf.Max(1, Mathf.RoundToInt(h));
            foreach (var gt in group.Textures)
            {
                if (cache.IsPureColor(gt.Record)) continue;
                var refRgba = cache.Region(gt.Record, island, out int rw, out int rh);
                if (!QualityEvaluator.Passes(refRgba, rw, rh, tw, th, gt.Record, gt.Role, bar)) return false;
            }
            return true;
        }
    }
}
