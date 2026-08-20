// ============================================================================
// ATO - atlas composer
// ATO - 图集合成
//
// Composes each packed page into a Texture2D:
//   - bilinear blit of every island's content (pure color -> solid fill);
//   - normals renormalized after resample (tangent data never recomputed);
//   - blank area filled by multi-source BFS edge pull-push (infinite
//     expansion); transparent pages keep alpha = 0 in the blanks.
// Also handles whole-image scaled textures (no-atlas mode / abandoned
// groups).
// 将每个已装页合成为 Texture2D：双线性 blit 各岛内容（纯色 -> 填充）；法线
// 重采样后重归一化（切线数据绝不重算）；空白区用多源 BFS 边缘 pull-push
// （无限外扩）填充；透明页空白区 alpha 保持 0。同时处理整图缩放贴图（无图
// 集模式/放弃图集化）。
// ============================================================================

#region

using System.Collections.Generic;
using net.fosa.AvatarTextureOptimizer.Editor.Analysis;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using net.fosa.AvatarTextureOptimizer.Editor.Packing;
using net.fosa.AvatarTextureOptimizer.Editor.Quality;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Atlas
{
    public static class AtlasComposer
    {
        /// <summary>Composes all packed pages + whole-texture scaled
        /// textures. 合成全部已装页 + 整图缩放贴图。</summary>
        public static void Compose(ATOContext ctx)
        {
            var an = ctx.Analysis;
            var c = ctx.Component;
            var log = ctx.Log;
            var decoder = new RegionDecoder(an);
            try
            {
                if (an.PackedResult != null)
                {
                    // snapshot main pages (mirrors are appended during compose)
                    // 快照主图页（镜像页在合成过程中追加）
                    var mainPages = new List<ATOPackedPage>();
                    foreach (var page in an.PackedResult.Pages)
                    {
                        if (page.IsMirrorRole == -1) mainPages.Add(page);
                    }
                    int i = 0;
                    foreach (var page in mainPages)
                    {
                        ctx.Session.Check("Atlas 图集合成");
                        ctx.Session.SetProgress((float) i / mainPages.Count);
                        ComposeMainPage(ctx, page, decoder, log);
                        ComposeMirrorPages(ctx, page, decoder, log);
                        i++;
                    }
                }

                // whole-image scaled textures  整图缩放贴图
                foreach (var (tid, s) in an.WholeTextureScales)
                {
                    ctx.Session.Check("Atlas 图集合成");
                    if (s >= 0.999f) continue;
                    var tref = an.Textures[tid];
                    var scaled = ScaleWholeTexture(tref.Texture, s, log);
                    an.ScaledTextures[tid] = scaled;
                }
            }
            finally
            {
                decoder.DisposeAll();
            }
        }

        // ------------------------------------------------------------------
        /// <summary>Composes the MAIN page: albedo (and utility) content
        /// only. Special roles go to mirror pages.
        /// 合成主图页：仅主色（与工具）内容。特殊角色进镜像页。</summary>
        private static void ComposeMainPage(ATOContext ctx, ATOPackedPage page,
            RegionDecoder decoder, ATOLog log)
        {
            var an = ctx.Analysis;
            var tg = an.TypeGroups[page.TypeGroupId];
            int pageIndex = an.PackedResult.Pages.IndexOf(page);
            var buffer = new float[page.W * page.H * 4];
            var covered = new bool[page.W * page.H];

            foreach (var item in page.Items)
            {
                foreach (var (group, lx0, ly0) in item.SubItems)
                {
                    foreach (var island in group.Islands)
                    {
                        if (island.AtlasPage != pageIndex) continue;
                        foreach (var tid in island.SampledTextureIds)
                        {
                            if (!tg.TextureIds.Contains(tid)) continue; // main page: albedo only
                            // 主图页：仅主色
                            BlitIsland(an, island, tid, page, buffer, covered, decoder);
                        }
                    }
                }
            }

            bool transparent = IsTransparentPage(an, page);
            PullPush(buffer, covered, page.W, page.H, transparent);
            page.Texture = ToTexture2D(buffer, page.W, page.H, tg.sRGB, tg.Filter,
                $"ATO_A_{page.TypeGroupId}", transparent);
            page.HasAlpha = HasAlpha(buffer, page.W, page.H);
        }

        /// <summary>Composes mirror pages for the special roles (normal /
        /// mask / emission) of the type group: identical normalized layout,
        /// page size = largest pool size not exceeding the quality-allowed
        /// upper bound (saves memory), never larger than the main page.
        /// 合成类型组特殊角色（法线/蒙版/自发光）的镜像页：归一化布局一致；
        /// 页尺寸 = 不超过质量允许上限的最大候选（省内存），且不超过主图页。</summary>
        private static void ComposeMirrorPages(ATOContext ctx, ATOPackedPage page,
            RegionDecoder decoder, ATOLog log)
        {
            var an = ctx.Analysis;
            var tg = an.TypeGroups[page.TypeGroupId];
            var roles = new Dictionary<Api.ATOTextureRole, List<int>>();
            foreach (var dict in tg.SpecialTextures.Values)
            {
                foreach (var (role, sid) in dict)
                {
                    if (!roles.TryGetValue(role, out var list))
                    {
                        list = new List<int>();
                        roles[role] = list;
                    }
                    if (!list.Contains(sid)) list.Add(sid);
                }
            }

            foreach (var (role, tids) in roles)
            {
                ctx.Session.Check("Atlas 图集合成");
                // upper bound: W such that every special island rect stays
                // within its quality-allowed px  上限：使每个特殊岛矩形不超过
                // 其质量允许像素
                float upper = page.W;
                foreach (var island in CollectIslandsOfPage(an, page))
                {
                    foreach (var tid in island.SampledTextureIds)
                    {
                        if (!tids.Contains(tid)) continue;
                        // pure-color islands have no quality constraint
                        // 纯色岛无质量约束
                        if (an.PureColorIslands.Contains((island.Id, tid))) continue;
                        if (!an.IslandScales.TryGetValue((island.Id, tid), out var allowed)) continue;
                        float rectNormW = island.AtlasW / (float) page.W;
                        if (rectNormW > 1e-6f)
                        {
                            upper = Mathf.Min(upper, allowed / rectNormW);
                        }
                    }
                }
                int mirrorW = LargestPoolSizeNotAbove(upper, page.W);
                if (mirrorW < 64) mirrorW = page.W;
                int mirrorH = Mathf.RoundToInt((float) page.H * mirrorW / page.W);
                mirrorH = Mathf.Clamp(mirrorH, 64, page.H);

                var buffer = new float[mirrorW * mirrorH * 4];
                var covered = new bool[mirrorW * mirrorH];
                foreach (var island in CollectIslandsOfPage(an, page))
                {
                    foreach (var tid in island.SampledTextureIds)
                    {
                        if (!tids.Contains(tid)) continue;
                        BlitIslandMirror(an, island, tid, mirrorW, mirrorH, page, buffer, covered, decoder);
                    }
                }
                bool transparent = role != Api.ATOTextureRole.Normal;
                PullPush(buffer, covered, mirrorW, mirrorH, transparent);
                var tex = ToTexture2D(buffer, mirrorW, mirrorH, role != Api.ATOTextureRole.Normal && tg.sRGB,
                    tg.Filter, $"ATO_{RoleName(role)}_{page.TypeGroupId}", transparent);
                // store mirror pages  保存镜像页
                var mirror = new ATOPackedPage
                {
                    TypeGroupId = page.TypeGroupId,
                    W = mirrorW,
                    H = mirrorH,
                    Texture = tex,
                    HasAlpha = HasAlpha(buffer, mirrorW, mirrorH),
                    IsMirrorRole = (int) role,
                };
                an.PackedResult.Pages.Add(mirror);
                page.MirrorRoles[role] = mirror;
                log.V(ATOLogMask.Atlas,
                    $"mirror page {RoleName(role)} #{page.TypeGroupId}: {mirrorW}x{mirrorH}. 镜像页。");
            }
        }

        private static List<ATOUVIsland> CollectIslandsOfPage(ATOAnalysis an, ATOPackedPage page)
        {
            int pageIndex = an.PackedResult.Pages.IndexOf(page);
            var list = new List<ATOUVIsland>();
            foreach (var island in an.Islands)
            {
                if (island.AtlasPage == pageIndex) list.Add(island);
            }
            return list;
        }

        private static int LargestPoolSizeNotAbove(float upper, int cap)
        {
            int best = 64;
            int max = AtlasPool.MaxSize();
            for (int s = 64; s <= max; s *= 2)
            {
                if (s <= upper && s <= cap) best = s;
                else break;
            }
            return best;
        }

        private static string RoleName(Api.ATOTextureRole role)
        {
            switch (role)
            {
                case Api.ATOTextureRole.Normal: return "N";
                case Api.ATOTextureRole.Mask: return "M";
                case Api.ATOTextureRole.Emission: return "E";
                default: return "U";
            }
        }

        private static void BlitIsland(
            ATOAnalysis an, ATOUVIsland island, int tid, ATOPackedPage page,
            float[] buffer, bool[] covered, RegionDecoder decoder)
        {
            BlitIslandTo(an, island, tid, page.W, page.H, 1f, 1f, buffer, covered, decoder);
        }

        /// <summary>Blits one island's content of one texture into a page
        /// (main: scale 1; mirror: scale = mirrorW/mainW).
        /// 将某岛某贴图内容 blit 进页（主图 scale=1；镜像 scale=镜像宽/主宽）。</summary>
        private static void BlitIslandTo(
            ATOAnalysis an, ATOUVIsland island, int tid, int pageW, int pageH,
            float sx, float sy, float[] buffer, bool[] covered, RegionDecoder decoder)
        {
            var region = decoder.Decode(island, tid);
            bool isNormal = region.IsNormal;

            int rw = Mathf.Max(1, Mathf.RoundToInt(island.AtlasW * sx));
            int rh = Mathf.Max(1, Mathf.RoundToInt(island.AtlasH * sy));
            float px = island.AtlasPos.x * sx;
            float py = island.AtlasPos.y * sy;

            if (an.PureColorIslands.Contains((island.Id, tid)))
            {
                float r = 0, g = 0, b = 0, a = 0;
                int n = 0;
                for (int p = 0; p < region.RGBA.Length; p += 4)
                {
                    r += region.RGBA[p];
                    g += region.RGBA[p + 1];
                    b += region.RGBA[p + 2];
                    a += region.RGBA[p + 3];
                    n++;
                }
                if (n > 0) { r /= n; g /= n; b /= n; a /= n; }
                FillRectF(buffer, pageW, pageH, px, py, rw, rh, r, g, b, a, covered);
                return;
            }

            var scaled = Bilinear.Resample(region.RGBA, region.W, region.H, rw, rh);
            if (isNormal) Renormalize(scaled);
            BlitRectF(buffer, pageW, pageH, scaled, rw, rh, px, py, island.Rot90, covered);
        }

        private static void BlitIslandMirror(
            ATOAnalysis an, ATOUVIsland island, int tid, int mirrorW, int mirrorH,
            ATOPackedPage mainPage, float[] buffer, bool[] covered, RegionDecoder decoder)
        {
            BlitIslandTo(an, island, tid, mirrorW, mirrorH,
                mirrorW / (float) mainPage.W, mirrorH / (float) mainPage.H,
                buffer, covered, decoder);
        }

        // ------------------------------------------------------------------
        private static void FillRectF(float[] buf, int W, int H, float fx, float fy, int w, int h,
            float r, float g, float b, float a, bool[] covered)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, W);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, H);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(fx + w), 0, W);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(fy + h), 0, H);
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * W + x) * 4;
                    buf[i] = r;
                    buf[i + 1] = g;
                    buf[i + 2] = b;
                    buf[i + 3] = a;
                    covered[y * W + x] = true;
                }
            }
        }

        private static void BlitRectF(float[] buf, int W, int H, float[] src, int sw, int sh,
            float fx, float fy, int rot, bool[] covered)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, W);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, H);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(fx + (rot == 1 ? sh : sw)), 0, W);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(fy + (rot == 1 ? sw : sh)), 0, H);
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int dx = x - Mathf.FloorToInt(fx);
                    int dy = y - Mathf.FloorToInt(fy);
                    int sxp, syp;
                    if (rot == 1)
                    {
                        sxp = dy;
                        syp = sh - 1 - dx;
                    }
                    else
                    {
                        sxp = dx;
                        syp = dy;
                    }
                    if (sxp < 0 || syp < 0 || sxp >= sw || syp >= sh) continue;
                    int s = (syp * sw + sxp) * 4;
                    int i = (y * W + x) * 4;
                    buf[i] = src[s];
                    buf[i + 1] = src[s + 1];
                    buf[i + 2] = src[s + 2];
                    buf[i + 3] = src[s + 3];
                    covered[y * W + x] = true;
                }
            }
        }

        private static bool IsTransparentPage(ATOAnalysis an, ATOPackedPage page)
        {
            var tg = an.TypeGroups[page.TypeGroupId];
            // transparent when any referring material of the albedos is non-opaque
            // 组内任一引用材质非不透明即透明
            foreach (var tid in tg.TextureIds)
            {
                foreach (var mat in an.Textures[tid].ReferringMaterials)
                {
                    if (an.Materials.TryGetValue(mat, out var info) && info.AlphaMode != 0) return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------
        private static Texture2D ToTexture2D(float[] buffer, int W, int H, bool srgb,
            FilterMode filter, string name, bool keepAlphaBlank)
        {
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp, // forced clamp  强制 Clamp
                hideFlags = HideFlags.HideAndDontSave,
            };
            var colors = new Color32[W * H];
            for (int i = 0; i < W * H; i++)
            {
                int o = i * 4;
                float r = buffer[o], g = buffer[o + 1], b = buffer[o + 2], a = buffer[o + 3];
                if (srgb)
                {
                    r = RegionDecoder.LinearToSrgb(r);
                    g = RegionDecoder.LinearToSrgb(g);
                    b = RegionDecoder.LinearToSrgb(b);
                }
                colors[i] = new Color32(
                    (byte) Mathf.Clamp(Mathf.RoundToInt(r * 255f), 0, 255),
                    (byte) Mathf.Clamp(Mathf.RoundToInt(g * 255f), 0, 255),
                    (byte) Mathf.Clamp(Mathf.RoundToInt(b * 255f), 0, 255),
                    (byte) Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
            }
            tex.SetPixels32(colors);
            tex.Apply(false, true);
            return tex;
        }

        private static bool HasAlpha(float[] buffer, int W, int H)
        {
            int step = Mathf.Max(1, (W * H) / 2048);
            for (int i = 0; i < W * H; i += step)
            {
                if (buffer[i * 4 + 3] < 0.999f) return true;
            }
            return false;
        }

        private static void Renormalize(float[] normal)
        {
            for (int i = 0; i < normal.Length; i += 4)
            {
                float x = normal[i], y = normal[i + 1], z = normal[i + 2];
                float len = Mathf.Sqrt(x * x + y * y + z * z);
                if (len > 1e-6f)
                {
                    normal[i] = x / len;
                    normal[i + 1] = y / len;
                    normal[i + 2] = z / len;
                }
            }
        }

        /// <summary>Multi-source BFS edge extension into uncovered pixels.
        /// RGB is extended; alpha extended too, except transparent pages
        /// where blank alpha stays 0.
        /// 多源 BFS 边缘外扩。RGB 外扩；alpha 同样外扩，透明页空白 alpha 保持 0。</summary>
        private static void PullPush(float[] buf, bool[] covered, int W, int H, bool transparent)
        {
            // flat int[] ring queue (memory-friendly vs Queue<Tuple>)
            // 扁平 int[] 环形队列（比元组队列省内存）
            int total = W * H;
            var queue = new int[total];
            int head = 0, tail = 0;
            var visited = new bool[total];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    if (covered[i])
                    {
                        visited[i] = true;
                        queue[tail++] = i;
                    }
                }
            }
            while (head < tail)
            {
                int i = queue[head++];
                int x = i % W;
                int o = i * 4;
                if (x > 0) TryVisit(i - 1, o);
                if (x < W - 1) TryVisit(i + 1, o);
                if (i >= W) TryVisit(i - W, o);
                if (i < total - W) TryVisit(i + W, o);
            }

            void TryVisit(int ni, int srcO)
            {
                if (visited[ni]) return;
                visited[ni] = true;
                int o = ni * 4;
                buf[o] = buf[srcO];
                buf[o + 1] = buf[srcO + 1];
                buf[o + 2] = buf[srcO + 2];
                buf[o + 3] = transparent ? 0f : buf[srcO + 3];
                queue[tail++] = ni;
            }
        }

        // ------------------------------------------------------------------
        /// <summary>Whole-image scale (no-atlas mode). 整图缩放（无图集模式）。</summary>
        public static Texture2D ScaleWholeTexture(Texture2D src, float s, ATOLog log)
        {
            int w = Mathf.Max(4, Mathf.RoundToInt(src.width * s));
            int h = Mathf.Max(4, Mathf.RoundToInt(src.height * s));
            var path = AssetDatabase.GetAssetPath(src);
            bool srgb = false;
            FilterMode filter = FilterMode.Bilinear;
            if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path, out var imp) && imp is TextureImporter ti)
            {
                srgb = ti.sRGB;
                filter = ti.filterMode;
            }
            var colors = src.GetPixels();
            var linear = new float[colors.Length * 4];
            for (int i = 0; i < colors.Length; i++)
            {
                linear[i * 4] = srgb ? RegionDecoder.SrgbToLinear(colors[i].r) : colors[i].r;
                linear[i * 4 + 1] = srgb ? RegionDecoder.SrgbToLinear(colors[i].g) : colors[i].g;
                linear[i * 4 + 2] = srgb ? RegionDecoder.SrgbToLinear(colors[i].b) : colors[i].b;
                linear[i * 4 + 3] = colors[i].a;
            }
            var scaled = Bilinear.Resample(linear, src.width, src.height, w, h);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = src.name + "_ATO_scaled",
                filterMode = filter,
                wrapMode = src.wrapMode,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var outColors = new Color32[w * h];
            for (int i = 0; i < w * h; i++)
            {
                float r = scaled[i * 4], g = scaled[i * 4 + 1], b = scaled[i * 4 + 2], a = scaled[i * 4 + 3];
                if (srgb)
                {
                    r = RegionDecoder.LinearToSrgb(r);
                    g = RegionDecoder.LinearToSrgb(g);
                    b = RegionDecoder.LinearToSrgb(b);
                }
                outColors[i] = new Color32(
                    (byte) Mathf.Clamp(Mathf.RoundToInt(r * 255f), 0, 255),
                    (byte) Mathf.Clamp(Mathf.RoundToInt(g * 255f), 0, 255),
                    (byte) Mathf.Clamp(Mathf.RoundToInt(b * 255f), 0, 255),
                    (byte) Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
            }
            tex.SetPixels32(outColors);
            tex.Apply(false, true);
            return tex;
        }
    }
}
