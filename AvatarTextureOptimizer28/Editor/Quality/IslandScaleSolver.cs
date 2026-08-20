using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: Solves, for every island of every UV group, the smallest scale that still meets the quality
    ///     profile for *all* textures bound to that UV. This is where the "bucket effect" rule lives:
    ///     one shared layout must satisfy the strictest texture in the group.
    ///
    ///     Search strategy (per island):
    ///       1. Short circuits: lossless profile keeps scale 1 and skips resampling entirely; a solid
    ///          island collapses straight to min(4, short side); a sub-11px island skips SSIM.
    ///       2. Texel density clamp derived from the island's real-world area bounds the search range,
    ///          so a tiny prop never gets a 4K allocation and a full body never gets blurred.
    ///       3. Uniform binary search for the smallest passing isotropic scale.
    ///       4. Two independent axis refinements, U then V, to exploit island anisotropy.
    ///
    /// ZH: 为每个 UV 组的每个岛求解"仍能让绑定到该 UV 的**全部**贴图满足质量配置"的最小缩放。
    ///     "木桶效应"规则就体现在这里：一套共享布局必须满足组内最严苛的那张贴图。
    ///
    ///     每个岛的搜索策略：
    ///       1. 短路：无损配置保持 1 并完全跳过重采样；纯色岛直接塌缩到 min(4, 短边)；小于 11px 的岛跳过 SSIM。
    ///       2. 由岛的真实世界面积推导的像素密度钳制会限定搜索范围，
    ///          既不会让小挂件拿到 4K 分配，也不会让全身贴图发糊。
    ///       3. 均匀二分搜索，求最小的可通过各向同性缩放。
    ///       4. 先 U 后 V 的双轴独立细化，以利用岛的各向异性。
    /// </summary>
    public sealed class IslandScaleSolver
    {
        private const int UniformIterations = 8;
        private const int AxisIterations = 6;

        private readonly GPUTextureIO _io;
        private readonly ATOLog _log;
        private readonly ATOProgress _progress;

        /// <summary>EN: Construct. ZH: 构造。</summary>
        public IslandScaleSolver(GPUTextureIO io, ATOLog log, ATOProgress progress)
        {
            _io = io;
            _log = log;
            _progress = progress;
        }

        /// <summary>EN: Per-texture evaluation context, resolved once per UV group. ZH: 每个 UV 组解析一次的逐贴图评估上下文。</summary>
        private sealed class TexEval
        {
            public AtoTexture Tex;
            public DecodedTexture Decoded;
            public AlphaMode AlphaMode;
            public float Cutoff;
            public bool AgNormal;
        }

        /// <summary>EN: Solve every island of a UV group. ZH: 求解一个 UV 组的所有岛。</summary>
        public void Solve(UVGroup group, in QualityProfile quality, float minDensity, float maxDensity)
        {
            if (group.Islands.Count == 0) return;

            if (quality.IsLossless)
            {
                // EN: Target quality 1 means "never rescale, never resample" for every texture class.
                // ZH: 目标质量为 1 意味着对所有贴图类别都"永不缩放、永不重采样"。
                foreach (var i in group.Islands) { i.ScaleU = 1f; i.ScaleV = 1f; }
                _log.Trace($"{group}: lossless profile, all islands kept at scale 1");
                return;
            }

            var evals = BuildEvals(group);
            if (evals.Count == 0)
            {
                foreach (var i in group.Islands) { i.ScaleU = 1f; i.ScaleV = 1f; }
                return;
            }

            var layout = group.LayoutSize;
            int solidCount = 0;

            for (int idx = 0; idx < group.Islands.Count; idx++)
            {
                _progress.ThrowIfCancelled();
                var island = group.Islands[idx];

                int baseW = Mathf.Max(1, Mathf.CeilToInt((island.UvMax.x - island.UvMin.x) * layout.x));
                int baseH = Mathf.Max(1, Mathf.CeilToInt((island.UvMax.y - island.UvMin.y) * layout.y));
                int shortSide = Mathf.Min(baseW, baseH);

                // ---- Solid short circuit ------------------------------------------------------------
                if (IsSolidEverywhere(evals, island))
                {
                    island.IsSolid = true;
                    int target = Mathf.Min(ATOConstants.SolidIslandMinSide, shortSide);
                    island.ScaleU = (float)Mathf.Min(target, baseW) / baseW;
                    island.ScaleV = (float)Mathf.Min(target, baseH) / baseH;
                    solidCount++;
                    continue;
                }

                // ---- Density clamp -------------------------------------------------------------------
                ComputeDensityBounds(island, baseW, baseH, minDensity, maxDensity, out var sLow, out var sHigh);

                // ---- Uniform binary search -----------------------------------------------------------
                float lo = sLow, hi = sHigh;
                float best = sHigh;
                if (!Evaluate(evals, island, sHigh, sHigh, layout, quality, shortSide))
                {
                    // EN: Even the largest allowed scale fails, which only happens when the density clamp
                    //     forces us below the quality target. Honour the clamp and keep the largest.
                    // ZH: 连允许的最大缩放都无法通过，只有当密度钳制把我们压到质量目标以下时才会发生。
                    //     此时尊重钳制，保留最大值。
                    island.ScaleU = island.ScaleV = sHigh;
                    _log.Trace($"{island}: density clamp dominates, forced to {sHigh:F3}");
                    continue;
                }

                for (int it = 0; it < UniformIterations && hi - lo > 0.005f; it++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (Evaluate(evals, island, mid, mid, layout, quality, shortSide)) { best = mid; hi = mid; }
                    else lo = mid;
                }

                // ---- Anisotropic refinement ------------------------------------------------------------
                float su = best, sv = best;
                su = RefineAxis(evals, island, layout, quality, shortSide, sLow, su, sv, refineU: true);
                sv = RefineAxis(evals, island, layout, quality, shortSide, sLow, su, sv, refineU: false);

                island.ScaleU = su;
                island.ScaleV = sv;
                _log.Trace($"{island}: solved scale ({su:F3},{sv:F3}) from base {baseW}x{baseH}");
            }

            _log.Detail($"{group}: {group.Islands.Count} islands solved ({solidCount} solid short-circuited)");
        }

        private float RefineAxis(List<TexEval> evals, UVIsland island, Vector2Int layout,
            in QualityProfile quality, int shortSide, float floor, float su, float sv, bool refineU)
        {
            float lo = floor;
            float hi = refineU ? su : sv;
            float best = hi;
            for (int it = 0; it < AxisIterations && hi - lo > 0.005f; it++)
            {
                float mid = (lo + hi) * 0.5f;
                bool ok = refineU
                    ? Evaluate(evals, island, mid, sv, layout, quality, shortSide)
                    : Evaluate(evals, island, su, mid, layout, quality, shortSide);
                if (ok) { best = mid; hi = mid; } else lo = mid;
            }
            return best;
        }

        private List<TexEval> BuildEvals(UVGroup group)
        {
            var list = new List<TexEval>();
            foreach (var kv in group.Textures)
            foreach (var tex in kv.Value)
            {
                var t = tex.Representative;
                if (t.Whitelisted) continue;

                // EN: Strictest alpha treatment across every material referencing this texture.
                // ZH: 引用该贴图的所有材质中最严苛的 alpha 处理方式。
                var mode = AlphaMode.Opaque;
                float cutoff = 1f;
                bool any = false;
                foreach (var u in group.Usages)
                {
                    if (u.Texture.Representative != t) continue;
                    mode = ShaderAnalyzer.Strictest(mode, u.AlphaMode);
                    // EN: The strictest cutoff is the smallest one: it keeps the most texels alive, so the
                    //     silhouette test is hardest to satisfy.
                    // ZH: 最严苛的 Cutoff 是最小的那个：它保留最多纹素，使轮廓测试最难满足。
                    if (u.AlphaMode == AlphaMode.Cutout) { cutoff = Mathf.Min(cutoff, u.Cutoff); any = true; }
                }
                if (!any) cutoff = 0.5f;

                list.Add(new TexEval
                {
                    Tex = t,
                    Decoded = _io.Decode(t.Source, t.SRGB),
                    AlphaMode = mode,
                    Cutoff = cutoff,
                    AgNormal = t.Class == TextureClass.Normal && !t.UsedChannels.B && t.UsedChannels.A,
                });
            }
            return list;
        }

        private static bool IsSolidEverywhere(List<TexEval> evals, UVIsland island)
        {
            foreach (var e in evals)
            {
                var rect = PixelRect(island, e.Decoded.Width, e.Decoded.Height);
                if (rect.width * rect.height > 1 << 20) return false;   // EN: too big to be worth testing. ZH: 太大，不值得测试。
                var tile = ImageOps.Extract(e.Decoded, rect);
                if (!ImageOps.IsSolid(tile, out _)) return false;
            }
            return true;
        }

        private static void ComputeDensityBounds(UVIsland island, int baseW, int baseH,
            float minDensity, float maxDensity, out float sLow, out float sHigh)
        {
            sLow = 1f / Mathf.Max(baseW, baseH);      // EN: never below one pixel. ZH: 不低于 1 像素。
            sHigh = 1f;

            if (island.WorldAreaM2 > 1e-8f)
            {
                double basePixels = (double)baseW * baseH;
                double minPixels = island.WorldAreaM2 * (double)minDensity * minDensity;
                double maxPixels = island.WorldAreaM2 * (double)maxDensity * maxDensity;

                // EN: Scale is linear, pixel count is quadratic, hence the square roots. Both bounds are
                //     additionally clamped by the physical size of the source texture, which is the hard
                //     ceiling we can never exceed without inventing detail.
                // ZH: 缩放是线性的而像素数是平方的，故取平方根。两个边界还会被源贴图的物理尺寸钳制，
                //     那是不凭空捏造细节就绝不能突破的硬上限。
                sLow = Mathf.Max(sLow, (float)Math.Sqrt(minPixels / basePixels));
                sHigh = Mathf.Min(1f, (float)Math.Sqrt(maxPixels / basePixels));
            }

            sHigh = Mathf.Clamp(sHigh, 1f / Mathf.Max(baseW, baseH), 1f);
            sLow = Mathf.Clamp(sLow, 1f / Mathf.Max(baseW, baseH), sHigh);
        }

        private bool Evaluate(List<TexEval> evals, UVIsland island, float su, float sv,
            Vector2Int layout, in QualityProfile quality, int shortSide)
        {
            foreach (var e in evals)
            {
                var rect = PixelRect(island, e.Decoded.Width, e.Decoded.Height);
                if (rect.width <= 0 || rect.height <= 0) continue;

                int tw = Mathf.Max(1, Mathf.RoundToInt(rect.width * su));
                int th = Mathf.Max(1, Mathf.RoundToInt(rect.height * sv));
                if (tw >= rect.width && th >= rect.height) continue;   // EN: no reduction, trivially passes. ZH: 没有缩减，必然通过。

                var reference = ImageOps.Extract(e.Decoded, rect);
                bool premultiply = e.Tex.Class == TextureClass.TransparentColor;

                Tile candidate;
                if (e.Tex.Class == TextureClass.Normal)
                {
                    // EN: Normal maps must be decoded, resampled and renormalised before re-encoding,
                    //     otherwise the averaged vectors shorten and the surface flattens.
                    // ZH: 法线贴图必须先解码、重采样、重归一化再编码，
                    //     否则平均后的向量会变短，表面会被压平。
                    var n = ImageOps.DecodeNormals(reference, e.AgNormal);
                    var nTile = ImageOps.EncodeNormals(n, reference.W, reference.H);
                    var small = ImageOps.Downsample(nTile, tw, th, false);
                    var smallN = ImageOps.DecodeNormals(small, false);
                    var renorm = ImageOps.EncodeNormals(smallN, small.W, small.H);
                    candidate = ImageOps.UpsampleBilinear(renorm, reference.W, reference.H);
                    var cn = ImageOps.DecodeNormals(candidate, false);
                    candidate = ImageOps.EncodeNormals(cn, candidate.W, candidate.H);
                }
                else
                {
                    candidate = ImageOps.RoundTrip(reference, tw, th, premultiply);
                }

                var m = QualityMetrics.Compare(reference, candidate, e.Tex.Class,
                    e.AlphaMode, e.Cutoff, e.Tex.UsedChannels, e.AgNormal);

                if (!QualityMetrics.Passes(m, quality, e.Tex.Class, e.AlphaMode, shortSide)) return false;
            }
            return true;
        }

        /// <summary>EN: Island UV bounds expressed as an integer pixel rectangle in a texture. ZH: 岛的 UV 边界在某贴图中的整数像素矩形。</summary>
        public static RectInt PixelRect(UVIsland island, int texW, int texH)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(island.UvMin.x * texW), 0, texW - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(island.UvMin.y * texH), 0, texH - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(island.UvMax.x * texW), x0 + 1, texW);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(island.UvMax.y * texH), y0 + 1, texH);
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }
    }
}
