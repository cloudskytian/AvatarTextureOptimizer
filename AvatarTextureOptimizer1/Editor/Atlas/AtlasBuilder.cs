// AtlasBuilder.cs / AtlasBuilder.cs
// Orchestrates rasterization, packing, blit, dilation and propagation of placement info.
// 协调光栅化、装箱、blit、外扩和放置信息传播。

using System;
using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using net.fosa.avatar_texture_optimizer.Editor.Groups;
using net.fosa.avatar_texture_optimizer.Editor.Util;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace net.fosa.avatar_texture_optimizer.Editor.Atlas
{
    public class AtlasTexture
    {
        public string Name;
        public int Width, Height;
        public Texture2D Texture;
        public List<(UVGroup group, Rect rect, bool rotated)> Placements = new();
        public bool HasAlpha;
        public bool IsNormal;
        public float Utilization;
        public TextureUsageFlags UsageFlags;
    }

    public static class AtlasBuilder
    {
        /// <summary>
        /// Compute atlas padding per spec: ceil(max_side/128), clamped to min 4px.
        /// 按规范计算图集padding：ceil(max_side/128)，最小4px钳制。
        /// </summary>
        public static int ComputePadding(int maxSide, int configuredPadding)
        {
            int computed = Mathf.CeilToInt(maxSide / 128f);
            return Mathf.Max(4, Mathf.Max(configuredPadding, computed));
        }

        public static List<AtlasTexture> BuildAll(AvatarAnalysisResult analysis, ATOLogger log, int configuredPadding, bool allowNPOT, int maxAtlasSize)
        {
            var atlases = new List<AtlasTexture>();
            var pool = BLFPacker.GenerateCandidatePool(maxAtlasSize, allowNPOT);

            long originalBytes = 0;
            long atlasBytes = 0;

            foreach (var tg in analysis.TypeGroups)
            {
                var items = BuildPackItems(tg, log);
                if (items.Count == 0) continue;

                // Compute padding based on max side of target items / 基于目标项目最大边计算padding
                int maxItemSide = 0;
                foreach (var it in items)
                    maxItemSide = Mathf.Max(maxItemSide, Mathf.Max(it.TargetPixelSize.x, it.TargetPixelSize.y));
                int padding = ComputePadding(maxItemSide, configuredPadding);

                var tgAtlases = BLFPacker.Pack(items, pool, padding, maxAtlasSize,
                    tg.IsNormal, tg.NeedsAlpha, "ATO_" + BuildAtlasName(tg), out var skipped);

                foreach (var atl in tgAtlases)
                {
                    atl.HasAlpha = tg.NeedsAlpha;
                    atl.IsNormal = tg.IsNormal;
                    BlitAtlas(atl, padding);
                    PullPushDilation.Dilate(atl.Texture, padding, tg.NeedsAlpha);
                    atlases.Add(atl);

                    long atlasByteCount = (long)atl.Width * atl.Height * 4;
                    long origByteCount = 0;
                    foreach (var pl in atl.Placements)
                        foreach (var isl in pl.group.Islands)
                            if (isl.SourceTexture != null)
                                origByteCount += (long)isl.SourceTexture.width * isl.SourceTexture.height * 4;
                    // Don't double-count source / 不要重复计算源
                    origByteCount = origByteCount / Mathf.Max(1, atl.Placements.Sum(p => p.group.Islands.Count(i => i.SourceTexture != null)));

                    atlasBytes += atlasByteCount;
                    log.AddAtlasStat(atl.Name, Math.Max(atl.Width, atl.Height), atl.Placements.Count, atl.Utilization, origByteCount, atlasByteCount);

                    // Propagate placement back to islands / 将放置信息回传到岛
                    foreach (var pl in atl.Placements)
                    {
                        foreach (var island in pl.group.Islands)
                        {
                            island.AssignedAtlas = atl;
                            island.AtlasRect = pl.rect;
                            island.Rotated = pl.rotated;
                        }
                        pl.group.TargetPixelRect = new RectInt(
                            Mathf.RoundToInt(pl.rect.x), Mathf.RoundToInt(pl.rect.y),
                            Mathf.RoundToInt(pl.rect.width), Mathf.RoundToInt(pl.rect.height));
                        pl.group.Rotated = pl.rotated;
                    }
                }

                foreach (var skip in skipped)
                {
                    log.LogWarning(ATOLocalization.T("warning.textureTooLargeForAtlas", skip.group.Id));
                    skip.group.FullyWhitelisted = true;
                    foreach (var island in skip.group.Islands) island.IsWhitelisted = true;
                }

                tg.Atlases.AddRange(tgAtlases);
            }

            // Accumulate original bytes across all source textures (unique)
            // 累计所有源贴图的原始字节（去重）
            var uniqueSrc = new HashSet<Texture2D>();
            foreach (var isl in analysis.Islands)
                if (isl.SourceTexture != null && !isl.IsWhitelisted) uniqueSrc.Add(isl.SourceTexture);
            foreach (var t in uniqueSrc)
                originalBytes += (long)t.width * t.height * 4;
            log.OriginalBytes = originalBytes;
            log.OptimizedBytes = atlasBytes;

            return atlases;
        }

        private static string BuildAtlasName(TextureTypeGroup tg)
        {
            string tag = "";
            if (tg.IsNormal) tag += "N_";
            if (tg.IsGrayscale) tag += "G_";
            if (tg.NeedsAlpha) tag += "A_";
            return "Atlas_" + tag;
        }

        private static List<PackItem> BuildPackItems(TextureTypeGroup tg, ATOLogger log)
        {
            var items = new List<PackItem>();
            foreach (var grp in tg.UvGroups)
            {
                if (grp.FullyWhitelisted) continue;

                // Compute union bbox in source texture coordinates (pixel space)
                // 计算源贴图坐标中的合并bbox（像素空间）
                int minX = int.MaxValue, minY = int.MaxValue;
                int maxX = 0, maxY = 0;
                Texture2D representative = null;
                foreach (var isl in grp.Islands)
                {
                    if (isl.SourceTexture == null) continue;
                    // Pick representative matching this type group (normal for normal group, etc.)
                    // 选择匹配此类型组的代表（法线组选法线等）
                    if (representative == null) representative = isl.SourceTexture;
                    int ix = Mathf.FloorToInt(isl.BoundsUV.xMin * isl.SourceTexture.width);
                    int iy = Mathf.FloorToInt(isl.BoundsUV.yMin * isl.SourceTexture.height);
                    int iw = Mathf.CeilToInt(isl.BoundsUV.width * isl.SourceTexture.width);
                    int ih = Mathf.CeilToInt(isl.BoundsUV.height * isl.SourceTexture.height);
                    if (ix < minX) minX = ix;
                    if (iy < minY) minY = iy;
                    if (ix + iw > maxX) maxX = ix + iw;
                    if (iy + ih > maxY) maxY = iy + ih;
                }
                if (representative == null) { grp.FullyWhitelisted = true; continue; }

                int srcW = Mathf.Max(1, maxX - minX);
                int srcH = Mathf.Max(1, maxY - minY);

                int tw = Mathf.Max(4, Mathf.RoundToInt(srcW * Mathf.Abs(grp.FinalScale.x)));
                int th = Mathf.Max(4, Mathf.RoundToInt(srcH * Mathf.Abs(grp.FinalScale.y)));

                // Build triangle pixel-space vertices for rasterization
                // 构建用于光栅化的三角形像素空间顶点
                var triVerts = new List<Vector2>();
                foreach (var isl in grp.Islands)
                {
                    if (isl.RendererEntry == null) continue;
                    Mesh workMesh = isl.RendererEntry.WorkingMesh;
                    if (workMesh == null) continue;

                    var uvList = new List<Vector2>();
                    workMesh.GetUVs(isl.UVChannel, uvList);
                    if (uvList.Count == 0) continue;

                    int ox = Mathf.FloorToInt(isl.BoundsUV.xMin * isl.SourceTexture.width);
                    int oy = Mathf.FloorToInt(isl.BoundsUV.yMin * isl.SourceTexture.height);
                    AddIslandTriangles(triVerts, isl, uvList, ox - minX, oy - minY, tw, th, srcW, srcH);
                }

                var item = new PackItem
                {
                    Group = grp,
                    TargetPixelSize = new Vector2Int(tw, th),
                };

                if (triVerts.Count >= 3)
                {
                    item.Mask = Rasterization.RasterizeTriangles(triVerts.ToArray(), tw, th, out item.GridW, out item.GridH);
                }
                else
                {
                    item.GridW = (tw + Rasterization.GRAN - 1) / Rasterization.GRAN;
                    item.GridH = (th + Rasterization.GRAN - 1) / Rasterization.GRAN;
                    int wpr = (item.GridW + 63) / 64;
                    item.Mask = new ulong[item.GridH * wpr];
                    for (int y = 0; y < item.GridH; y++)
                        for (int x = 0; x < item.GridW; x++)
                        {
                            int word = y * wpr + x / 64;
                            int bit = x % 64;
                            item.Mask[word] |= 1UL << bit;
                        }
                }
                // Allow rotation for normals too (tangent rotation handled in MeshProcessor)
                // 也允许法线旋转（切线旋转在MeshProcessor中处理）
                item.AllowRotation = true;
                items.Add(item);
            }
            return items;
        }

        private static void AddIslandTriangles(List<Vector2> triVerts, UVIsland isl, List<Vector2> uvs, int srcOX, int srcOY, int tw, int th, int srcW, int srcH)
        {
            if (uvs == null || uvs.Count == 0 || isl.Triangles == null || isl.Triangles.Count < 3) return;
            float uMin = isl.BoundsUV.xMin, vMin = isl.BoundsUV.yMin;
            float uRange = Mathf.Max(0.0001f, isl.BoundsUV.width);
            float vRange = Mathf.Max(0.0001f, isl.BoundsUV.height);
            for (int i = 0; i + 2 < isl.Triangles.Count; i += 3)
            {
                int i0 = isl.Triangles[i];
                int i1 = isl.Triangles[i+1];
                int i2 = isl.Triangles[i+2];
                if (i0 < 0 || i0 >= uvs.Count || i1 < 0 || i1 >= uvs.Count || i2 < 0 || i2 >= uvs.Count) continue;
                triVerts.Add(ToPixel(uvs[i0], uMin, vMin, uRange, vRange, tw, th));
                triVerts.Add(ToPixel(uvs[i1], uMin, vMin, uRange, vRange, tw, th));
                triVerts.Add(ToPixel(uvs[i2], uMin, vMin, uRange, vRange, tw, th));
            }
        }

        private static Vector2 ToPixel(Vector2 uv, float uMin, float vMin, float uRange, float vRange, int tw, int th)
        {
            float u = (uv.x - uMin) / uRange * tw;
            float v = (uv.y - vMin) / vRange * th;
            return new Vector2(Mathf.Clamp(u, 0, tw - 1), Mathf.Clamp(v, 0, th - 1));
        }

        private static void BlitAtlas(AtlasTexture atl, int padding)
        {
            var result = new Texture2D(atl.Width, atl.Height, TextureFormat.RGBA32, true, false);
            Color[] clear = new Color[atl.Width * atl.Height];
            Color bg = atl.HasAlpha ? new Color(0, 0, 0, 0) : new Color(0.5f, 0.5f, 1f, 1f);
            for (int i = 0; i < clear.Length; i++) clear[i] = bg;
            result.SetPixels(clear);

            foreach (var pl in atl.Placements)
            {
                var grp = pl.group;
                if (grp.Islands.Count == 0) continue;

                // Composite all islands in this UV group from their respective source textures
                // 从各自的源贴图合成此UV组中的所有岛
                foreach (var isl in grp.Islands)
                {
                    if (isl.SourceTexture == null || !isl.SourceTexture.isReadable) continue;
                    int sx = Mathf.Clamp(Mathf.FloorToInt(isl.BoundsUV.xMin * isl.SourceTexture.width), 0, isl.SourceTexture.width-1);
                    int sy = Mathf.Clamp(Mathf.FloorToInt(isl.BoundsUV.yMin * isl.SourceTexture.height), 0, isl.SourceTexture.height-1);
                    int sw = Mathf.Clamp(isl.OriginalPixelSize.x, 1, isl.SourceTexture.width - sx);
                    int sh = Mathf.Clamp(isl.OriginalPixelSize.y, 1, isl.SourceTexture.height - sy);
                    Color[] src;
                    try { src = isl.SourceTexture.GetPixels(sx, sy, sw, sh); }
                    catch { continue; }
                    int tw = Mathf.RoundToInt(pl.rect.width);
                    int th = Mathf.RoundToInt(pl.rect.height);
                    if (tw < 1 || th < 1) continue;
                    var dst = BilinearResize(src, sw, sh, tw, th, pl.rotated);

                    int dgx = Mathf.RoundToInt(pl.rect.x);
                    int dgy = Mathf.RoundToInt(pl.rect.y);
                    for (int y = 0; y < th; y++)
                        for (int x = 0; x < tw; x++)
                        {
                            int dx = dgx + x;
                            int dy = dgy + y;
                            if (dx < 0 || dy < 0 || dx >= atl.Width || dy >= atl.Height) continue;
                            Color srcC = dst[y * tw + x];
                            // For color/alpha: write when alpha non-zero; for normals: always write;
                            // When multiple islands cover the same atlas pixel, last writer wins (they should be UV-disjoint anyway)
                            // 颜色/alpha：alpha非零时写入；法线：总是写入；多岛覆盖同一像素时最后写入者赢（UV本应不相交）
                            if (atl.IsNormal || srcC.a > 0.001f || !atl.HasAlpha)
                                result.SetPixel(dx, dy, srcC);
                        }
                }
            }
            result.Apply(true, false);
            result.name = atl.Name;
            result.wrapMode = TextureWrapMode.Clamp;
            atl.Texture = result;
        }

        private static Color[] BilinearResize(Color[] src, int sw, int sh, int dw, int dh, bool rotate90)
        {
            Color[] dst = new Color[dw * dh];
            if (rotate90)
            {
                // Rotate 90° clockwise when placing / 放置时顺时针旋转90度
                // (u,v) in [0,1]x[0,1] -> (1-v, u) mapping
                for (int y = 0; y < dh; y++)
                    for (int x = 0; x < dw; x++)
                    {
                        float u = (float)y / Mathf.Max(1, dh - 1);
                        float v = 1f - (float)x / Mathf.Max(1, dw - 1);
                        float sx_f = u * (sw - 1);
                        float sy_f = v * (sh - 1);
                        int x0 = Mathf.Clamp(Mathf.FloorToInt(sx_f), 0, sw - 1);
                        int y0 = Mathf.Clamp(Mathf.FloorToInt(sy_f), 0, sh - 1);
                        int x1 = Mathf.Min(x0 + 1, sw - 1);
                        int y1 = Mathf.Min(y0 + 1, sh - 1);
                        float fx = sx_f - x0, fy = sy_f - y0;
                        Color c00 = src[y0*sw+x0], c10 = src[y0*sw+x1], c01 = src[y1*sw+x0], c11 = src[y1*sw+x1];
                        Color c0 = Color.Lerp(c00, c10, fx);
                        Color c1 = Color.Lerp(c01, c11, fx);
                        dst[y*dw+x] = Color.Lerp(c0, c1, fy);
                    }
            }
            else
            {
                for (int y = 0; y < dh; y++)
                    for (int x = 0; x < dw; x++)
                    {
                        float u = (float)x / Mathf.Max(1, dw - 1) * (sw - 1);
                        float v = (float)y / Mathf.Max(1, dh - 1) * (sh - 1);
                        int x0 = Mathf.Clamp(Mathf.FloorToInt(u), 0, sw - 1);
                        int y0 = Mathf.Clamp(Mathf.FloorToInt(v), 0, sh - 1);
                        int x1 = Mathf.Min(x0 + 1, sw - 1);
                        int y1 = Mathf.Min(y0 + 1, sh - 1);
                        float fx = u - x0, fy = v - y0;
                        Color c00 = src[y0*sw+x0], c10 = src[y0*sw+x1], c01 = src[y1*sw+x0], c11 = src[y1*sw+x1];
                        Color c0 = Color.Lerp(c00, c10, fx);
                        Color c1 = Color.Lerp(c01, c11, fx);
                        dst[y*dw+x] = Color.Lerp(c0, c1, fy);
                    }
            }
            return dst;
        }
    }
}
