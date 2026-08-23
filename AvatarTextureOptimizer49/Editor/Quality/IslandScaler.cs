using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>One (layout island × texture) instance with its scaled result. / 一个（布局岛×贴图）实例及其缩放结果。</summary>
    internal class IslandInstance
    {
        internal UvIsland island;
        internal Texture2D texture;
        /// <summary>All roles this texture is used as (each must pass its metrics — strictest wins). / 该贴图的全部用途（逐一评估取最严）。</summary>
        internal readonly List<TexCategory> categories = new List<TexCategory>();
        /// <summary>Category used for atlas storage/resampling. / 图集存储与重采样用类别。</summary>
        internal TexCategory storageCategory;
        internal bool srgb;
        internal bool hasAlpha;
        internal RectInt region;          // pixel rect in the source texture / 源贴图像素矩形
        internal Color32[] sourceRegion;  // raw region copy / 区域原始像素
        internal int finalW, finalH;      // shared layout size of the island / 岛的共享布局尺寸
        internal float ownMinScaleX = 1f, ownMinScaleY = 1f; // own minimal passing scale / 自身最小达标缩放
        internal Color32[] atlasBytes;    // final bytes for the atlas (or whole-texture path) / 图集字节
        internal bool verbatim;
        internal bool pureColor;
        internal string note;

        internal bool HasCategory(TexCategory c) => categories.Contains(c);
    }

    /// <summary>
    /// Island quality scaling: per-texture binary search (uniform first, then per-axis anisotropic
    /// refinement), pure-color short-circuit to min(4, side), density clamps (px/m from the
    /// island's max world area, clamped by the original on-disk size), barrel effect across all
    /// textures of the UV group (final size = max of each texture's minimum passing size, never
    /// above the largest original). Near-lossless skips scaling entirely (verbatim copy).
    /// / 岛质量缩放：逐贴图二分（先均匀后双轴细化）、纯色短路、密度钳制、UV组木桶效应、近无损原样拷贝。
    /// </summary>
    internal class IslandScaler
    {
        private const int MinIslandSide = 4;

        internal List<IslandInstance> Instances = new List<IslandInstance>();

        internal void ScaleGroup(UvGroup group, TextureStore store, QualityEvaluator evaluator,
            AtoSettings settings, Action<int, int> progress)
        {
            var q = settings.quality;
            bool nearLossless = q.IsNearLossless;

            int done = 0;
            foreach (var island in group.islands)
            {
                progress?.Invoke(done, group.islands.Count);
                done++;

                var instances = new List<IslandInstance>();
                foreach (var kv in group.textures)
                {
                    var tex = kv.Key;
                    var storage = kv.Value;
                    var info = store.GetImportInfo(tex);

                    var inst = new IslandInstance
                    {
                        island = island,
                        texture = tex,
                        storageCategory = storage,
                        srgb = storage == TexCategory.Color && info.sRGB,
                    };
                    if (group.usageCategories.TryGetValue(tex, out var cats))
                        inst.categories.AddRange(cats);
                    else
                        inst.categories.Add(storage);
                    instances.Add(inst);
                    Instances.Add(inst);

                    // region in source pixels (bottom-left origin, matching GetPixels32) / 源图像素区域
                    var pixels = store.GetPixels(tex);
                    int tw = tex.width, th = tex.height;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(island.uvBounds.x * tw), 0, tw - 1);
                    int y0 = Mathf.Clamp(Mathf.FloorToInt(island.uvBounds.y * th), 0, th - 1);
                    int x1 = Mathf.Clamp(Mathf.CeilToInt(island.uvBounds.xMax * tw), x0 + 1, tw);
                    int y1 = Mathf.Clamp(Mathf.CeilToInt(island.uvBounds.yMax * th), y0 + 1, th);
                    inst.region = new RectInt(x0, y0, x1 - x0, y1 - y0);
                    inst.sourceRegion = CopyRegion(pixels, tw, inst.region);
                    inst.hasAlpha = DetectAlpha(inst.sourceRegion);

                    // ---- pure color short-circuit / 纯色短路 ----
                    if (!nearLossless && q.msSsim < 1f && MetricJobs.IsPureColor(inst.sourceRegion))
                    {
                        inst.pureColor = true;
                        int side = Mathf.Min(MinIslandSide, Mathf.Min(inst.region.width, inst.region.height));
                        inst.ownMinScaleX = side / (float)inst.region.width;
                        inst.ownMinScaleY = side / (float)inst.region.height;
                        continue;
                    }

                    if (nearLossless)
                    {
                        inst.verbatim = true;
                        inst.ownMinScaleX = inst.ownMinScaleY = 1f;
                        continue;
                    }

                    // ---- density clamps / 密度钳制 ----
                    float len = Mathf.Sqrt(Mathf.Max(island.worldArea, 1e-8f));
                    int minSideW = Mathf.Min(MinIslandSide, inst.region.width);
                    int minSideH = Mathf.Min(MinIslandSide, inst.region.height);
                    int loCapW = Mathf.Clamp(Mathf.RoundToInt(len * settings.minPixelsPerMeter), minSideW, inst.region.width);
                    int loCapH = Mathf.Clamp(Mathf.RoundToInt(len * settings.minPixelsPerMeter), minSideH, inst.region.height);
                    int hiCapW = Mathf.Clamp(Mathf.RoundToInt(len * settings.maxPixelsPerMeter), minSideW, inst.region.width);
                    int hiCapH = Mathf.Clamp(Mathf.RoundToInt(len * settings.maxPixelsPerMeter), minSideH, inst.region.height);
                    // never above the on-disk original size / 不超过原贴图物理尺寸
                    hiCapW = Mathf.Min(hiCapW, inst.region.width);
                    hiCapH = Mathf.Min(hiCapH, inst.region.height);
                    loCapW = Mathf.Min(loCapW, hiCapW);
                    loCapH = Mathf.Min(loCapH, hiCapH);

                    float loX = loCapW / (float)inst.region.width;
                    float loY = loCapH / (float)inst.region.height;
                    float hiX = hiCapW / (float)inst.region.width;
                    float hiY = hiCapH / (float)inst.region.height;

                    // ---- uniform binary search: max passing scale / 均匀二分：最大达标缩放 ----
                    float sLo = Mathf.Min(loX, loY);
                    float sHi = Mathf.Min(hiX, hiY, 1f);
                    if (sHi <= sLo)
                    {
                        inst.ownMinScaleX = hiX;
                        inst.ownMinScaleY = hiY;
                        inst.note = "density-clamped";
                        continue;
                    }

                    var (uniform, okU) = SearchMaxPassing(inst, evaluator, q, group, sLo, sHi);
                    if (!okU) inst.note = "quality floor unreachable; kept minimum density / 质量下限不可达，保留最小密度";

                    // ---- per-axis refinement (anisotropic): min passing per axis / 双轴独立细化：各轴最小达标 ----
                    float sx = uniform;
                    if (okU && loX < uniform) sx = SearchMinPassingX(inst, evaluator, q, group, loX, uniform, uniform);
                    float sy = uniform;
                    if (okU && loY < uniform) sy = SearchMinPassingY(inst, evaluator, q, group, loY, uniform, sx);

                    inst.ownMinScaleX = sx;
                    inst.ownMinScaleY = sy;
                }

                // ---- barrel effect: shared layout size = max over textures / 木桶效应：取最大尺寸 ----
                int finalW = 0, finalH = 0;
                foreach (var inst in instances)
                {
                    int w = Mathf.Max(1, Mathf.RoundToInt(inst.region.width * inst.ownMinScaleX));
                    int h = Mathf.Max(1, Mathf.RoundToInt(inst.region.height * inst.ownMinScaleY));
                    finalW = Mathf.Max(finalW, w);
                    finalH = Mathf.Max(finalH, h);
                }

                foreach (var inst in instances)
                {
                    inst.finalW = finalW;
                    inst.finalH = finalH;
                    if (inst.verbatim && finalW == inst.region.width && finalH == inst.region.height)
                        inst.atlasBytes = inst.sourceRegion; // copy untouched / 原样拷贝
                    else
                        inst.atlasBytes = evaluator.MakeAtlasBytes(inst.sourceRegion,
                            inst.region.width, inst.region.height, finalW, finalH,
                            inst.category, inst.srgb);
                }
            }
        }

        /// <summary>Max uniform scale in [lo,hi] passing all metrics (monotone). / 区间内最大达标均匀缩放。</summary>
        private (float, bool) SearchMaxPassing(IslandInstance inst, QualityEvaluator evaluator,
            QualityParams q, UvGroup group, float lo, float hi)
        {
            if (!TryPass(inst, evaluator, q, group, lo, lo)) return (lo, false);
            for (int it = 0; it < 7; it++)
            {
                float mid = 0.5f * (lo + hi);
                if (mid <= lo || mid >= hi) break;
                if (TryPass(inst, evaluator, q, group, mid, mid)) lo = mid;
                else hi = mid;
            }
            return (lo, true);
        }

        /// <summary>Min x-scale in [lo,start] passing with fixed y (monotone). / 固定y时最小达标的x缩放。</summary>
        private float SearchMinPassingX(IslandInstance inst, QualityEvaluator evaluator,
            QualityParams q, UvGroup group, float lo, float start, float sy)
        {
            float hi = start;
            if (!TryPass(inst, evaluator, q, group, lo, sy)) return start;
            for (int it = 0; it < 7; it++)
            {
                float mid = 0.5f * (lo + hi);
                if (mid <= lo || mid >= hi) break;
                if (TryPass(inst, evaluator, q, group, mid, sy)) hi = mid;
                else lo = mid;
            }
            return hi;
        }

        /// <summary>Min y-scale in [lo,start] passing with fixed x (monotone). / 固定x时最小达标的y缩放。</summary>
        private float SearchMinPassingY(IslandInstance inst, QualityEvaluator evaluator,
            QualityParams q, UvGroup group, float lo, float start, float sx)
        {
            float hi = start;
            if (!TryPass(inst, evaluator, q, group, sx, lo)) return start;
            for (int it = 0; it < 7; it++)
            {
                float mid = 0.5f * (lo + hi);
                if (mid <= lo || mid >= hi) break;
                if (TryPass(inst, evaluator, q, group, sx, mid)) hi = mid;
                else lo = mid;
            }
            return hi;
        }

        /// <summary>Candidate test at (sx,sy). / 候选缩放测试。</summary>
        private bool TryPass(IslandInstance inst, QualityEvaluator evaluator, QualityParams q,
            UvGroup group, float sx, float sy)
        {
            int dw = Mathf.Clamp(Mathf.RoundToInt(inst.region.width * sx), 1, inst.region.width);
            int dh = Mathf.Clamp(Mathf.RoundToInt(inst.region.height * sy), 1, inst.region.height);
            if (dw == inst.region.width && dh == inst.region.height) return true;

            var scaled = evaluator.Downsample(inst.sourceRegion, inst.region.width, inst.region.height,
                dw, dh, inst.storageCategory, inst.srgb);
            var test = evaluator.Upsample(scaled, dw, dh, inst.region.width, inst.region.height,
                inst.storageCategory, inst.hasAlpha && QualityEvaluator.UsesAlpha(group.alphaCandidates));

            // every role must pass its own metrics (strictest wins) / 每个用途逐一评估，全部达标
            foreach (var cat in inst.categories)
            {
                if (!evaluator.Evaluate(cat, inst.srgb, inst.sourceRegion, test,
                        inst.region.width, inst.region.height, group.alphaCandidates, q, inst.hasAlpha, out var m))
                    return false;
            }
            return true;
        }

        private static Color32[] CopyRegion(Color32[] src, int srcW, RectInt r)
        {
            var dst = new Color32[r.width * r.height];
            for (int y = 0; y < r.height; y++)
            {
                int srcRow = (r.y + y) * srcW + r.x;
                int dstRow = y * r.width;
                Array.Copy(src, srcRow, dst, dstRow, r.width);
            }
            return dst;
        }

        private static bool DetectAlpha(Color32[] px)
        {
            for (int i = 0; i < px.Length; i += 7) // sparse scan is enough / 稀疏扫描足够
                if (px[i].a < 250) return true;
            return false;
        }
    }
}
