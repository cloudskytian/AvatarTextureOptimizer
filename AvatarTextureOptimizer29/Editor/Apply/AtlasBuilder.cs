// Atlas composition: pages per (kind, slot); layout shared across pages; area-average
// resample (linear space, premultiplied alpha; normals decode-average-renormalize);
// pull-push bleed; compression & import params applied by TextureParams.
// 图集合成：按（类别,槽位）分页；所有页共享布局；面积平均重采样（线性空间、透明预乘；
// 法线解码-平均-重归一化）；pull-push 渗色；压缩与导入参数由 TextureParams 处理。
//
// Multiple textures of the same kind on one island (animation variants) get distinct
// page slots (graph coloring by co-usage) so each keeps the identical layout.
// 同岛同类别的多张贴图（动画变体）分到不同页槽（按共用关系着色），布局完全一致。

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato.editor
{
    internal class AtlasBuildResult
    {
        internal AtlasLayout layout;
        // kind -> slots -> page texture / 类别 -> 槽位 -> 页贴图
        internal readonly Dictionary<AtoTexCategory, List<Texture2D>> pages =
            new Dictionary<AtoTexCategory, List<Texture2D>>();
        internal readonly Dictionary<Texture2D, Texture2D> pageTempPixels = new Dictionary<Texture2D, Texture2D>();
    }

    internal static class AtlasBuilder
    {
        /// <summary>source texture -> (result, kind, slot). / 源贴图 -> (结果,类别,槽位)。</summary>
        internal static readonly Dictionary<Texture2D, (AtlasBuildResult res, AtoTexCategory kind, int slot)>
            Placement = new Dictionary<Texture2D, (AtlasBuildResult, AtoTexCategory, int)>();

        internal static readonly List<AtlasBuildResult> Results = new List<AtlasBuildResult>();

        internal static void Build(AtoSession s)
        {
            using var _ = ATOLog.Scope("ComposeAtlases");
            Placement.Clear();
            Results.Clear();
            if (!s.component.generateAtlas) return;

            int idx = 0;
            foreach (var atlas in s.atlases)
            {
                Progress.Report("compose", idx / (float)Mathf.Max(1, s.atlases.Count), $"atlas {idx + 1}/{s.atlases.Count}");
                idx++;
                Results.Add(BuildOne(s, atlas));
            }
        }

        private static AtlasBuildResult BuildOne(AtoSession s, AtlasLayout atlas)
        {
            var res = new AtlasBuildResult { layout = atlas };

            // ---- slot coloring per kind / 按类别着色槽位 ----
            var slotOf = new Dictionary<Texture2D, int>();
            foreach (var kindGroup in atlas.placements
                         .SelectMany(p => p.island.textures.Select(t => (p.island, t)))
                         .GroupBy(x => CategoryOf(s, x.t)))
            {
                var kind = kindGroup.Key;
                // greedy: textures co-used on an island get different slots / 同岛不能同槽
                var forbidden = new Dictionary<Texture2D, HashSet<int>>();
                foreach (var g in kindGroup.GroupBy(x => x.island))
                {
                    var texs = g.Select(x => x.t).ToList();
                    foreach (var t in texs)
                    {
                        if (!forbidden.TryGetValue(t, out var f)) forbidden[t] = f = new HashSet<int>();
                        var used = new HashSet<int>();
                        foreach (var other in texs.Where(o => !ReferenceEquals(o, t)))
                            if (slotOf.TryGetValue(other, out var so))
                                used.Add(so);
                        for (int i = 0; ; i++)
                            if (!used.Contains(i))
                            {
                                f.Add(i); // candidate
                                break;
                            }
                    }
                }

                foreach (var t in kindGroup.Select(x => x.t).Distinct())
                {
                    if (slotOf.ContainsKey(t)) continue;
                    int slot = 0;
                    if (forbidden.TryGetValue(t, out var f) && f.Count > 0) slot = f.Min();
                    slotOf[t] = slot;
                }

                int slots = Mathf.Max(1, slotOf.Where(kv => CategoryOf(s, kv.Key) == kind)
                    .Select(kv => kv.Value).DefaultIfEmpty(0).Max() + 1);

                var pageList = new List<Texture2D>();
                var (pw, ph) = PageSizeOf(kind, atlas);
                for (int i = 0; i < slots; i++)
                {
                    var page = new Texture2D(pw, ph, TextureFormat.RGBA32, true, LinearOf(kind))
                    {
                        name = $"ATO_{atlas.typeGroupKey}_{kind}_{Results.Count}_{i}",
                        wrapMode = TextureWrapMode.Clamp, // forced / 强制
                        filterMode = atlas.placements.Count > 0
                            ? FilterModeOf(atlas)
                            : FilterMode.Bilinear,
                        anisoLevel = AnisoOf(s, atlas),
                    };
                    pageList.Add(page);
                    res.pages[kind] = pageList;
                }
            }

            // ---- compose / 合成 ----
            foreach (var kv in res.pages)
                FillPages(s, atlas, res, kv.Key, kv.Value, slotOf);

            foreach (var t in slotOf)
                Placement[t.Key] = (res, CategoryOf(s, t.Key), t.Value);

            return res;
        }

        private static AtoTexCategory CategoryOf(AtoSession s, Texture2D t) =>
            s.texInfos.TryGetValue(t, out var ti) ? ti.category : AtoTexCategory.Opaque;

        private static bool LinearOf(AtoTexCategory kind) =>
            kind == AtoTexCategory.Normal || kind == AtoTexCategory.Gray;

        private static int AnisoOf(AtoSession s, AtlasLayout atlas)
        {
            // highest aniso among sources (params take the best of sources) / 参数取来源最优
            int best = 1;
            foreach (var t in atlas.textures)
                best = Mathf.Max(best, t.anisoLevel);
            return best;
        }

        private static FilterMode FilterModeOf(AtlasLayout atlas)
        {
            // type group key contains filterMode; take the sharpest present / 取最锐利
            return (FilterMode)int.Parse(atlas.typeGroupKey.Split('|')[1]);
        }

        private static (int, int) PageSizeOf(AtoTexCategory kind, AtlasLayout atlas)
        {
            switch (kind)
            {
                case AtoTexCategory.Normal: return (atlas.normalW, atlas.normalH);
                case AtoTexCategory.Gray: return (atlas.maskW, atlas.maskH);
                default: return (atlas.pageW, atlas.pageH);
            }
        }

        // ------------------------------------------------------------------
        private static void FillPages(AtoSession s, AtlasLayout atlas, AtlasBuildResult res,
            AtoTexCategory kind, List<Texture2D> pages, Dictionary<Texture2D, int> slotOf)
        {
            var (pw, ph) = PageSizeOf(kind, atlas);
            float pageScale = kind == AtoTexCategory.Normal ? atlas.normalPageScale
                : kind == AtoTexCategory.Gray ? atlas.maskPageScale : 1f;

            for (int slot = 0; slot < pages.Count; slot++)
            {
                var page = pages[slot];
                var buf = new Color32[pw * ph];
                var cover = new float[pw * ph];
                foreach (var p in atlas.placements)
                foreach (var tex in p.island.textures)
                {
                    if (CategoryOf(s, tex) != kind) continue;
                    if (!slotOf.TryGetValue(tex, out int slot2) || slot2 != slot) continue;
                    if (!s.texInfos.TryGetValue(tex, out var ti)) continue;

                    ComposePlacement(s, p, tex, ti, buf, cover, pw, ph, pageScale);
                }

                // pull-push bleed / 渗色
                using var px = new NativeArray<Color32>(buf, Allocator.TempJob);
                using var cv = new NativeArray<float>(cover, Allocator.TempJob);
                var job = new PullPushJob
                {
                    pixels = px, coverage = cv, width = pw, height = ph,
                    keepAlphaZero = kind != AtoTexCategory.Normal && kind != AtoTexCategory.Gray
                                    && AtlasHasAlpha(s, atlas, kind),
                };
                job.Schedule().Complete();
                px.CopyTo(buf);

                page.SetPixels32(buf);
                page.Apply(true); // mip chain / 生成mip
                res.pageTempPixels[page] = page; // params applied later / 参数后置处理
                float util = Utilization(atlas, pw, ph);
                ATOLog.Info($"page {page.name}: {pw}x{ph} utilization {util:P1} " +
                            $"({atlas.placements.Count} islands, sources: {string.Join(",", atlas.textures.Select(t => t.name).Take(8))})");
            }
        }

        private static bool AtlasHasAlpha(AtoSession s, AtlasLayout atlas, AtoTexCategory kind)
        {
            foreach (var t in atlas.textures)
                if (CategoryOf(s, t) == kind && s.texInfos.TryGetValue(t, out var ti) && ti.hasAlphaContent)
                    return true;
            return false;
        }

        internal static float Utilization(AtlasLayout atlas, int pw, int ph)
        {
            long used = 0;
            foreach (var p in atlas.placements) used += (long)p.rect.width * p.rect.height;
            return (float)used / ((long)pw * ph);
        }

        /// <summary>Compose one island of one texture into a page buffer.
        /// 将一张贴图的一个岛合成进页面缓冲。</summary>
        private static void ComposePlacement(AtoSession s, Placement p, Texture2D tex, TexInfo ti,
            Color32[] buf, float[] cover, int pw, int ph, float pageScale)
        {
            var cp = TexturePixels.Get(tex, ti.category == AtoTexCategory.Normal);
            if (cp == null) return;

            // source region / 源区域
            int sx = Mathf.Clamp(Mathf.FloorToInt(p.island.uvBounds.xMin * cp.width), 0, cp.width - 1);
            int sy = Mathf.Clamp(Mathf.FloorToInt(p.island.uvBounds.yMin * cp.height), 0, cp.height - 1);
            int sw = Mathf.Clamp(Mathf.CeilToInt(p.island.uvBounds.width * cp.width), 1, cp.width - sx);
            int sh = Mathf.Clamp(Mathf.CeilToInt(p.island.uvBounds.height * cp.height), 1, cp.height - sy);

            // dest rect on this page / 目标矩形（含次要页缩放）
            int dx = Mathf.RoundToInt(p.rect.xMin * pageScale);
            int dy = Mathf.RoundToInt(p.rect.yMin * pageScale);
            int dw = Mathf.Max(1, Mathf.RoundToInt(p.rect.width * pageScale));
            int dh = Mathf.Max(1, Mathf.RoundToInt(p.rect.height * pageScale));
            dw = Mathf.Min(dw, pw - Mathf.Min(dx, pw - 1));
            dh = Mathf.Min(dh, ph - Mathf.Min(dy, ph - 1));
            bool rotated = p.rotated;

            bool premult = ti.hasAlphaContent;
            bool srgb = cp.srgb && ti.category != AtoTexCategory.Normal;

            // resample un-rotated buffer / 以未旋转方向重采样，再按需转置写入
            int aw = rotated ? dh : dw; // buffer (unrotated footprint) dims / 缓冲=未旋转足印
            int ah = rotated ? dw : dh;
            var tmp = new Color32[aw * ah];
            using var srcN = new NativeArray<Color32>(cp.pixels, Allocator.TempJob);
            using var dstN = new NativeArray<Color32>(tmp.Length, Allocator.TempJob);
            using var sizeN = new NativeArray<int2>(1, Allocator.TempJob);
            sizeN[0] = new int2(aw, ah);

            var job = new DownsampleJob
            {
                src = srcN, srcW = cp.width, srcH = cp.height,
                region = new int4(sx, sy, sw, sh),
                premultiply = premultiply, srgb = srgb,
                dst = dstN, dstSize = sizeN,
            };
            job.Schedule().Complete();
            dstN.CopyTo(tmp);

            if (ti.category == AtoTexCategory.Normal)
                NormalizeNormalRegion(tmp, aw, cp.normalLayout);

            // write into page (transpose for 90°) / 写入页面（90°时转置）
            for (int v = 0; v < dh; v++)
                for (int u = 0; u < dw; u++)
                {
                    int bu = rotated ? v : u;
                    int bv = rotated ? u : v;
                    int px = dx + u, py = dy + v;
                    if (px < 0 || py < 0 || px >= pw || py >= ph) continue;
                    buf[py * pw + px] = tmp[bv * aw + bu];
                    cover[py * pw + px] = 1f;
                }
        }

        /// <summary>Renormalize averaged normals & re-encode to source layout.
        /// 重归一化平均后的法线并按源布局重新编码。</summary>
        private static void NormalizeNormalRegion(Color32[] tmp, int w, NormalLayout layout)
        {
            for (int i = 0; i < tmp.Length; i++)
            {
                var c = tmp[i];
                float x, y;
                switch (layout)
                {
                    case NormalLayout.RG: x = c.r / 255f; y = c.g / 255f; break;
                    case NormalLayout.AG: x = c.a / 255f; y = c.g / 255f; break;
                    default: x = c.r / 255f; y = c.g / 255f; break;
                }

                float2 xy = new float2(x, y) * 2f - 1f;
                float z = math.sqrt(math.max(0f, 1f - math.dot(xy, xy)));
                var n = math.normalizesafe(new float3(xy.x, xy.y, z), new float3(0, 0, 1));
                byte bx = (byte)Mathf.RoundToInt((n.x * 0.5f + 0.5f) * 255);
                byte by = (byte)Mathf.RoundToInt((n.y * 0.5f + 0.5f) * 255);
                byte bz = (byte)Mathf.RoundToInt((n.z * 0.5f + 0.5f) * 255);
                // pages always encode RG(x,y,z,1); target-format swizzle happens in
                // TextureParams.PrepackNormal before compression
                // 页统一编码为 RG(x,y,z,1)；目标格式转换在压缩前的 PrepackNormal 完成
                tmp[i] = new Color32(bx, by, bz, 255);
            }
        }
    }
}
