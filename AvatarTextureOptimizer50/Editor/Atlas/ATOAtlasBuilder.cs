// -----------------------------------------------------------------------------
// ATOAtlasBuilder.cs — compose atlas textures from placements.
// ATOAtlasBuilder.cs —— 由放置结果合成图集贴图。
//
// One BASE atlas texture holds every unit's islands. Additional LAYER atlases share
// the exact same normalized layout: one per (texture, role) pair sampled by the
// islands — normals / gray masks / extra colors / animated swap-ins. Layers may
// uniformly shrink (kept ≥ min padding) when their own quality allows.
// 一个基础图集承载所有单元的岛；附加层图集共享完全一致的归一化布局：每个被岛采样的
// （贴图, 角色）对一个层——法线/灰度/附加色/动画换入贴图。层可在自身质量允许时整体
// 缩小（保持不低于最小 padding）。
//
// Normal islands placed with 90° rotation get an (ny,-nx) swizzle to compensate
// (tangents are NEVER recomputed, per spec).
// 旋转90°放置的法线岛做 (ny,-nx) 通道交换补偿（规格：绝不重算切线）。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class ATOAtlasBuilder
    {
        public static void BuildAll(ATOBuildState st)
        {
            foreach (var atlas in st.atlases.ToList())
                BuildOne(atlas, st);
        }

        private static void BuildOne(AtlasResult atlas, ATOBuildState st)
        {
            int w = atlas.width, h = atlas.height;

            // ---------------- base layer / 主色层 ----------------
            var basePixels = new Color32[w * h];
            foreach (var isl in atlas.islands)
            {
                var baseTex = isl.UnitBaseTex();
                if (baseTex != null) FillIsland(basePixels, w, h, isl, baseTex, st, 1f, false);
            }

            var baseSrc = FirstUnitBase(atlas);
            var baseTexObj = CreateAtlasTexture(basePixels, w, h, atlas, st, baseSrc,
                baseSrc != null ? baseSrc.texClass : TexClass.AlbedoOpaque,
                baseSrc == null || baseSrc.IsSRGB, LayerKind.Base);
            atlas.baseLayer = new AtlasLayer
            {
                kind = LayerKind.Base, sourceTex = baseSrc, texture = baseTexObj,
                width = w, height = h,
            };

            // ---------------- counterpart layers per (tex,role) / 每个（贴图,角色）一个层 ----------------
            var pairs = atlas.islands
                .SelectMany(i => i.sampledTextures)
                .Where(t => !t.tex.SkipOptimization && t.role != TexRole.Main)
                .Select(t => (t.tex, t.role))
                .Distinct()
                .ToList();

            foreach (var (tex, role) in pairs)
            {
                float r = ComputeLayerRatio(atlas, tex, role, st);
                r = Mathf.Clamp(r, Mathf.Min(1f, (float)atlas.padding * 2f / Mathf.Min(w, h)), 1f);
                int lw = Mathf.Max(4, Mathf.RoundToInt(w * r));
                int lh = Mathf.Max(4, Mathf.RoundToInt(h * r));

                var lp = new Color32[lw * lh];
                bool any = false;
                foreach (var isl in atlas.islands)
                {
                    if (!isl.sampledTextures.Any(t => t.tex == tex && t.role == role)) continue;
                    FillIsland(lp, lw, lh, isl, tex, st, r, role == TexRole.Normal);
                    any = true;
                }

                if (!any) continue;

                var cls = role == TexRole.Normal ? TexClass.NormalMap
                    : role == TexRole.Gray ? TexClass.GrayMask
                    : tex.texClass;
                var layerTex = CreateAtlasTexture(lp, lw, lh, atlas, st, tex, cls,
                    role != TexRole.Normal && tex.IsSRGB, RoleToKind(role));
                atlas.layers.Add(new AtlasLayer
                {
                    kind = RoleToKind(role), sourceTex = tex, texture = layerTex,
                    width = lw, height = lh, scaleVsBase = r,
                });
            }

            // ---------------- variant layers (animated swap mains) / 变体层（动画换入主色） ----------------
            var variants = atlas.islands.SelectMany(i => i.VariantTexs()).Distinct().ToList();
            foreach (var v in variants)
            {
                var vp = new Color32[w * h];
                foreach (var isl in atlas.islands)
                    if (isl.VariantTexs().Contains(v))
                        FillIsland(vp, w, h, isl, v, st, 1f, false);

                var layerTex = CreateAtlasTexture(vp, w, h, atlas, st, v, v.texClass, v.IsSRGB,
                    LayerKind.Variant);
                atlas.layers.Add(new AtlasLayer
                {
                    kind = LayerKind.Variant, sourceTex = v, texture = layerTex,
                    width = w, height = h, scaleVsBase = 1f,
                });
            }

            // utilization / 利用率
            long used = atlas.islands.Sum(i => (long)Mathf.Max(1, i.scaledSize.x) * Mathf.Max(1, i.scaledSize.y));
            atlas.baseLayer.usedRatio = Mathf.Clamp01(used / (float)(w * h));
            ATOLog.Info($"Atlas #{atlas.id} [{atlas.typeKey}] {w}x{h} pad={atlas.padding} " +
                        $"islands={atlas.islands.Count} util={atlas.baseLayer.usedRatio:P1} " +
                        $"layers={atlas.layers.Count}");
        }

        internal static LayerKind RoleToKind(TexRole role) => role switch
        {
            TexRole.Normal => LayerKind.Normal,
            TexRole.Gray => LayerKind.Gray,
            TexRole.ExtraColor => LayerKind.ExtraColor,
            _ => LayerKind.Variant,
        };

        private static TexInfo FirstUnitBase(AtlasResult atlas)
        {
            foreach (var isl in atlas.islands)
            {
                var b = isl.UnitBaseTex();
                if (b != null) return b;
            }

            return null;
        }

        // ================================================================= //

        /// <summary>Fill one island's pixels into an atlas buffer.
        /// 将一个岛的像素填入图集缓冲。</summary>
        private static void FillIsland(Color32[] dst, int atlasW, int atlasH, IslandInfo isl,
            TexInfo src, ATOBuildState st, float ratio, bool isNormal)
        {
            if (src == null) return;
            var buf = ATOQuality.GetBuffer(src, st);
            if (buf == null) return;

            var srcRect = ATOQuality.IslandRect(isl, src);

            int cx = Mathf.RoundToInt(isl.cellRect.x * IslandRaster.Cell * ratio);
            int cy = Mathf.RoundToInt(isl.cellRect.y * IslandRaster.Cell * ratio);
            int cw = Mathf.Max(1, Mathf.RoundToInt(isl.cellRect.width * IslandRaster.Cell * ratio));
            int ch = Mathf.Max(1, Mathf.RoundToInt(isl.cellRect.height * IslandRaster.Cell * ratio));

            if (cx + cw > atlasW) cw = atlasW - cx;
            if (cy + ch > atlasH) ch = atlasH - cy;
            if (cw <= 0 || ch <= 0) return;

            bool lossless = isl.losslessCopy || (cw >= srcRect.width && ch >= srcRect.height);

            Color32[] content;
            if (lossless)
            {
                content = CopyClipped(buf, srcRect, cw, ch);
            }
            else
            {
                content = new Color32[cw * ch];
                using var srcNa = new NativeArray<Color32>(
                    CopyClipped(buf, srcRect, srcRect.width, srcRect.height), Allocator.TempJob);
                using var dstNa = new NativeArray<Color32>(content, Allocator.TempJob);
                new ATOQualityJobs.DownsampleJob
                {
                    src = srcNa, srcW = srcRect.width, srcH = srcRect.height,
                    srcX = 0, srcY = 0, srcWd = srcRect.width, srcHt = srcRect.height,
                    dstW = cw, dstH = ch,
                    premultiply = src.texClass == TexClass.AlbedoAlpha && !isNormal,
                    dst = dstNa,
                }.Schedule(dstNa.Length, 64).Complete();
                dstNa.CopyTo(content);
            }

            if (isNormal)
            {
                var srcFmt = src.source != null ? src.source.format : TextureFormat.RGBA32;
                var targetFmt = AtlasNormalFormat(st);
                bool dxtnm = ATOPlatform.UsesDxtNm(st.settings.platform);
                for (int i = 0; i < content.Length; i++)
                {
                    var n = ATONormalCodec.Decode(content[i], srcFmt);
                    if (isl.rotated) n = new Vector3(n.y, -n.x, n.z); // 90° compensation / 旋转补偿
                    content[i] = ATONormalCodec.EncodeFor(ATONormalCodec.EncodeRgb(n), targetFmt, dxtnm);
                }
            }
            else if (isl.rotated)
            {
                content = Rotate90(content, cw, ch);
                (cw, ch) = (ch, cw);
            }

            for (int y = 0; y < ch && y < atlasH - cy; y++)
            for (int x = 0; x < cw && x < atlasW - cx; x++)
                dst[(cy + y) * atlasW + cx + x] = content[y * cw + x];
        }

        private static Color32[] CopyClipped(PixelBuffer buf, RectInt r, int outW, int outH)
        {
            var outp = new Color32[outW * outH];
            for (int y = 0; y < outH; y++)
            {
                int sy = r.y + Mathf.Min(r.height - 1, y * r.height / Mathf.Max(1, outH));
                for (int x = 0; x < outW; x++)
                {
                    int sx = r.x + Mathf.Min(r.width - 1, x * r.width / Mathf.Max(1, outW));
                    outp[y * outW + x] = buf.pixels[sy * buf.width + sx];
                }
            }

            return outp;
        }

        private static Color32[] Rotate90(Color32[] src, int w, int h)
        {
            var dst = new Color32[src.Length];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                dst[x * h + (h - 1 - y)] = src[y * w + x];
            return dst;
        }

        private static TextureFormat AtlasNormalFormat(ATOBuildState st)
        {
            var (f, _) = ATOPlatform.Resolve(ATOPlatform.EffectiveFormats(st).normalMap,
                TexClass.NormalMap, st.settings.platform, true);
            return f;
        }

        /// <summary>Layer shrink probe: largest ratio where ALL islands sampling this
        /// (tex,role) still pass. Bounded probes (1, .75, .5, .25) for v0.1 speed.
        /// 层缩小探测：所有采样该（贴图,角色）的岛仍达标的最大比例。v0.1 用限次探测。</summary>
        private static float ComputeLayerRatio(AtlasResult atlas, TexInfo tex, TexRole role,
            ATOBuildState st)
        {
            foreach (var r in new[] { 1f, 0.75f, 0.5f, 0.25f })
            {
                bool ok = true;
                foreach (var isl in atlas.islands)
                {
                    if (!isl.sampledTextures.Any(t => t.tex == tex && t.role == role)) continue;

                    var saved = isl.sampledTextures.ToList();
                    isl.sampledTextures.RemoveAll(t => !(t.tex == tex && t.role == role));
                    float s = Mathf.Max(isl.scaledSize.x, 1) / (float)Mathf.Max(isl.origSize.x, 1);
                    bool pass = ATOQuality.Evaluate(isl, Vector2.one * (r * s), st);
                    isl.sampledTextures.Clear();
                    isl.sampledTextures.AddRange(saved);
                    if (!pass) { ok = false; break; }
                }

                if (ok) return r;
            }

            return 1f;
        }

        /// <summary>Create, encode, bleed, save & parameter-apply one atlas texture.
        /// 创建、编码、渗色、保存并应用参数。</summary>
        private static Texture2D CreateAtlasTexture(Color32[] px, int w, int h, AtlasResult atlas,
            ATOBuildState st, TexInfo srcTex, TexClass cls, bool srgb, LayerKind kind)
        {
            if (kind == LayerKind.Normal) cls = TexClass.NormalMap;
            if (srgb) px = ATOGpu.LinearToSrgb(px);

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, !srgb)
            {
                name = $"ATO_Atlas_{(srcTex != null ? srcTex.source.name : "group")}_{atlas.id}_{kind}",
            };
            tex.SetPixels32(px);
            ATOGpu.PullPushBleed(tex, st.gpu);
            ATOTextureParams.Apply(tex, cls, st, tex.name);
            st.assetSaver.SaveAsset(tex);
            return tex;
        }
    }

    /// <summary>Island→texture helpers / 岛→贴图辅助。</summary>
    internal static class IslandInfoExt
    {
        /// <summary>The unit base texture recorded by the planner (rest material main).
        /// 规划器记录的单元基础贴图（静态材质主色）。</summary>
        public static TexInfo UnitBaseTex(this IslandInfo isl) => isl.unitBase;

        public static IEnumerable<TexInfo> VariantTexs(this IslandInfo isl) =>
            isl.variants ?? (isl.variants = new List<TexInfo>());
    }
}
