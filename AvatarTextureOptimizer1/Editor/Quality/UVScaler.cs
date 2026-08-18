// UVScaler.cs / UVScaler.cs
// Binary-search based per-UV-island scaler using real pixel sampling (QualityEvaluator).
// Target size is determined by the worst metric across all layers of the UV group (barrel effect),
// clamped by pixel-density limits, and never exceeding the original source size.
// Solid-color islands short-circuit to min(4, original). Near-lossless skips scaling entirely.
// 使用真实像素采样(QualityEvaluator)的二分搜索逐UV岛缩放器。
// 目标尺寸由UV组所有层中最差指标决定（木桶效应），受像素密度限制钳制，且绝不超过原始源尺寸。
// 纯色岛短路到min(4,原始)；近无损完全跳过缩放。

using System.Collections.Generic;
using UnityEngine;
using net.fosa.avatar_texture_optimizer;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using net.fosa.avatar_texture_optimizer.Editor.Groups;
using net.fosa.avatar_texture_optimizer.Editor.Util;

namespace net.fosa.avatar_texture_optimizer.Editor.Quality
{
    public static class UVScaler
    {
        public static void ComputeTargetScales(AvatarAnalysisResult analysis, ATOLogger log)
        {
            var settings = analysis.Settings;
            var qt = GetQualityTargets(settings.qualityPreset, settings.customThresholds);
            float minDensity = (float)settings.minPixelDensity;
            float maxDensity = (float)settings.maxPixelDensity;
            var animRes = analysis.Animation;

            foreach (var grp in analysis.UvGroups)
            {
                if (grp.FullyWhitelisted && !grp.PartiallyWhitelisted)
                {
                    // Fully whitelisted (all textures whitelisted): keep original size, skip scaling entirely
                    // 完全白名单（所有贴图都白名单）：保持原始尺寸，完全跳过缩放
                    AssignOriginalSize(grp);
                    grp.FinalScale = Vector2.one;
                    continue;
                }
                // Partially whitelisted or normal group: compute target scales via quality search
                // (partially-WL groups still scale non-WL textures via whole-texture mode; atlas UV repack
                // will be skipped because FullyWhitelisted=true, but scale values are still needed)
                // 部分白名单或普通组：通过质量搜索计算目标缩放
                // （部分WL组因为FullyWhitelisted=true会跳过图集UV重打包，但仍需要scale值给整图缩放）

                // Compute original size (max across islands) / 计算原始尺寸（跨岛取最大）
                int origW = 1, origH = 1;
                float worldSideLen = 0f;
                bool hasAlpha = false;
                bool isNormal = false;
                bool isGrayscale = false;
                bool isCutout = false;
                float cutoff = 0.5f;

                float maxWorldArea = 0;
                foreach (var island in grp.Islands)
                {
                    if (island.OriginalPixelSize.x > origW) origW = island.OriginalPixelSize.x;
                    if (island.OriginalPixelSize.y > origH) origH = island.OriginalPixelSize.y;
                    if (island.WorldArea > maxWorldArea) maxWorldArea = island.WorldArea;
                    if (island.IsAlpha) hasAlpha = true;
                    if (island.NeedsNormalRotation) isNormal = true;
                    if (island.Cutoff > cutoff) cutoff = island.Cutoff;
                }
                // Detect cutout: group is cutout if any island sits on a cutout material (Cutoff < 1 and alpha present)
                // 检测cutout：任一岛在cutout材质上（Cutoff<1且有alpha）则组为cutout
                foreach (var island in grp.Islands)
                {
                    var me = island.RendererEntry != null && island.MaterialSlot < island.RendererEntry.Materials.Length
                        ? island.RendererEntry.Materials[island.MaterialSlot] : null;
                    if (me != null && me.AlphaMode == AlphaMode.Cutout) isCutout = true;
                }
                worldSideLen = Mathf.Sqrt(Mathf.Max(0.000001f, maxWorldArea));
                worldSideLen = Mathf.Max(0.0001f, worldSideLen);
                float minPxSide = worldSideLen * minDensity;
                float maxPxSide = worldSideLen * maxDensity;

                grp.EffectiveQuality = qt;

                if (qt.IsNearLossless)
                {
                    grp.TargetPixelRect = new RectInt(0, 0, origW, origH);
                    grp.FinalScale = Vector2.one;
                    foreach (var island in grp.Islands)
                        island.ScaledPixelSize = new Vector2Int(origW, origH);
                    continue;
                }

                // Check solid color on each source texture; if all source regions are solid, short-circuit
                // 检查每个源纹理的纯色；若所有源区域都是纯色，短路
                bool allSolid = true;
                foreach (var island in grp.Islands)
                {
                    if (island.SourceTexture == null) continue;
                    if (!island.SourceTexture.isReadable) { allSolid = false; break; }
                    var px = island.SourceTexture.GetPixels(
                        Mathf.Clamp(Mathf.FloorToInt(island.BoundsUV.xMin * island.SourceTexture.width), 0, island.SourceTexture.width-1),
                        Mathf.Clamp(Mathf.FloorToInt(island.BoundsUV.yMin * island.SourceTexture.height), 0, island.SourceTexture.height-1),
                        Mathf.Clamp(island.OriginalPixelSize.x, 1, island.SourceTexture.width),
                        Mathf.Clamp(island.OriginalPixelSize.y, 1, island.SourceTexture.height));
                    var rgn = new RectInt(0, 0, island.OriginalPixelSize.x, island.OriginalPixelSize.y);
                    if (!QualityEvaluator.IsSolidColor(px, island.OriginalPixelSize.x, island.OriginalPixelSize.y, rgn))
                    { allSolid = false; break; }
                }
                if (allSolid && origW > 4 && origH > 4)
                {
                    int minSide = Mathf.Min(4, Mathf.Min(origW, origH));
                    int target = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(maxPxSide, Mathf.Max(minPxSide, minSide))), 4, Mathf.Min(origW, origH));
                    grp.TargetPixelRect = new RectInt(0, 0, target, target);
                    grp.IsSolidColor = true;
                    grp.FinalScale = new Vector2((float)target / Mathf.Max(1, origW), (float)target / Mathf.Max(1, origH));
                    foreach (var island in grp.Islands)
                        island.ScaledPixelSize = new Vector2Int(target, target);
                    continue;
                }

                // Binary search uniform scale / 二分搜索均匀缩放
                int lo = Mathf.Max(4, Mathf.RoundToInt(minPxSide));
                int hi = Mathf.Clamp(Mathf.Max(lo+1, Mathf.RoundToInt(maxPxSide)), 4, Mathf.Max(origW, origH));
                int best = hi;

                // Pre-fetch source pixels from representative island's texture / 从代表岛的纹理预取源像素
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (GroupPassesAtSize(grp, mid, mid, qt, isNormal, isGrayscale, hasAlpha, isCutout, cutoff))
                    { best = mid; hi = mid - 1; }
                    else lo = mid + 1;
                }

                // Anisotropic refinement / 各向异性细化
                int bestX = best, bestY = best;
                // X axis / X轴
                int lx = Mathf.Max(4, Mathf.RoundToInt(minPxSide));
                int hx = Mathf.Clamp(best, 4, origW);
                bestX = hx;
                while (lx <= hx)
                {
                    int mid = (lx + hx) / 2;
                    if (GroupPassesAtSize(grp, mid, bestY, qt, isNormal, isGrayscale, hasAlpha, isCutout, cutoff))
                    { bestX = mid; hx = mid - 1; }
                    else lx = mid + 1;
                }
                // Y axis / Y轴
                int ly = Mathf.Max(4, Mathf.RoundToInt(minPxSide));
                int hy = Mathf.Clamp(best, 4, origH);
                bestY = hy;
                while (ly <= hy)
                {
                    int mid = (ly + hy) / 2;
                    if (GroupPassesAtSize(grp, bestX, mid, qt, isNormal, isGrayscale, hasAlpha, isCutout, cutoff))
                    { bestY = mid; hy = mid - 1; }
                    else ly = mid + 1;
                }

                grp.TargetPixelRect = new RectInt(0, 0, bestX, bestY);
                grp.FinalScale = new Vector2((float)bestX / Mathf.Max(1, origW), (float)bestY / Mathf.Max(1, origH));
                foreach (var island in grp.Islands)
                {
                    int iw = Mathf.Clamp(Mathf.RoundToInt(island.OriginalPixelSize.x * grp.FinalScale.x), 4, island.OriginalPixelSize.x);
                    int ih = Mathf.Clamp(Mathf.RoundToInt(island.OriginalPixelSize.y * grp.FinalScale.y), 4, island.OriginalPixelSize.y);
                    island.ScaledPixelSize = new Vector2Int(iw, ih);
                }
            }
        }

        private static bool GroupPassesAtSize(UVGroup grp, int tw, int th, QualityTarget qt, bool isNormal, bool isGrayscale, bool hasAlpha, bool isCutout, float cutoff)
        {
            foreach (var island in grp.Islands)
            {
                if (island.SourceTexture == null || !island.SourceTexture.isReadable) return true; // conservative
                if (tw >= island.OriginalPixelSize.x && th >= island.OriginalPixelSize.y) continue;
                try
                {
                    int sx = Mathf.Clamp(Mathf.FloorToInt(island.BoundsUV.xMin * island.SourceTexture.width), 0, island.SourceTexture.width-1);
                    int sy = Mathf.Clamp(Mathf.FloorToInt(island.BoundsUV.yMin * island.SourceTexture.height), 0, island.SourceTexture.height-1);
                    int rw = Mathf.Clamp(island.OriginalPixelSize.x, 1, island.SourceTexture.width - sx);
                    int rh = Mathf.Clamp(island.OriginalPixelSize.y, 1, island.SourceTexture.height - sy);
                    var px = island.SourceTexture.GetPixels(sx, sy, rw, rh);
                    var rgn = new RectInt(0, 0, rw, rh);
                    bool pass = QualityEvaluator.PassesQuality(px, rw, rh, rgn, tw, th, isNormal, isGrayscale, hasAlpha, isCutout, cutoff,
                        premultiplyAlpha: hasAlpha && !isCutout, target: qt);
                    if (!pass) return false;
                }
                catch
                {
                    return true; // conservative: accept on read errors
                }
            }
            return true;
        }

        private static void AssignOriginalSize(UVGroup grp)
        {
            int w = 1, h = 1;
            foreach (var i in grp.Islands)
            {
                if (i.OriginalPixelSize.x > w) w = i.OriginalPixelSize.x;
                if (i.OriginalPixelSize.y > h) h = i.OriginalPixelSize.y;
            }
            grp.TargetPixelRect = new RectInt(0, 0, w, h);
            foreach (var i in grp.Islands) i.ScaledPixelSize = new Vector2Int(w, h);
        }

        public static QualityTarget GetQualityTargets(QualityPreset preset, CustomQualityThresholds custom)
        {
            var q = new QualityTarget();
            switch (preset)
            {
                case QualityPreset.VeryLow:
                    q.MsSSIM = 0.90f; q.DeltaE = 10f; q.NormalAngleDeg = 12f;
                    q.AlphaRMSE = 0.12f; q.CutoutIoU = 0.94f; q.GrayscaleRMSE = 0.15f; break;
                case QualityPreset.Low:
                    q.MsSSIM = 0.94f; q.DeltaE = 6f; q.NormalAngleDeg = 8f;
                    q.AlphaRMSE = 0.08f; q.CutoutIoU = 0.96f; q.GrayscaleRMSE = 0.10f; break;
                case QualityPreset.Medium:
                    q.MsSSIM = 0.97f; q.DeltaE = 3.5f; q.NormalAngleDeg = 5f;
                    q.AlphaRMSE = 0.04f; q.CutoutIoU = 0.98f; q.GrayscaleRMSE = 0.05f; break;
                case QualityPreset.High:
                    q.MsSSIM = 0.985f; q.DeltaE = 2f; q.NormalAngleDeg = 3f;
                    q.AlphaRMSE = 0.02f; q.CutoutIoU = 0.99f; q.GrayscaleRMSE = 0.02f; break;
                case QualityPreset.VeryHigh:
                    q.MsSSIM = 0.995f; q.DeltaE = 1f; q.NormalAngleDeg = 1.5f;
                    q.AlphaRMSE = 0.01f; q.CutoutIoU = 0.995f; q.GrayscaleRMSE = 0.01f; break;
                case QualityPreset.Custom:
                    q.MsSSIM = custom.msSSIM; q.DeltaE = custom.deltaE; q.NormalAngleDeg = custom.normalAngleDeg;
                    q.AlphaRMSE = custom.alphaRMSE; q.CutoutIoU = custom.cutoutIoU; q.GrayscaleRMSE = custom.grayscaleRMSE;
                    q.IsNearLossless = Mathf.Abs(custom.msSSIM - 1f) < 0.0001f
                                      && custom.deltaE < 0.01f && custom.normalAngleDeg < 0.01f
                                      && custom.alphaRMSE < 0.0001f && Mathf.Abs(custom.cutoutIoU - 1f) < 0.0001f;
                    break;
            }
            return q;
        }
    }
}
