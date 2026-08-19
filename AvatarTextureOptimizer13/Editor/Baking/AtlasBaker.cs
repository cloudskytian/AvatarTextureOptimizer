// ATO — Avatar Texture Optimizer
// Bakes packed queues into atlas textures: resamples each island's source region (linear,
// premultiplied-alpha area average) into its placed position with rotation, fills empty
// regions via pull-push edge dilation, and saves the atlas into the NDMF asset container.
// 将装箱队列烘焙为图集贴图：把每个岛的源区域（线性、预乘 alpha 面积平均）重采样到放置位置
// （含旋转），用 pull-push 边缘外扩填充空白区域，并保存到 NDMF 资产容器。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Bakes atlases from pack results. 由装箱结果烘焙图集。
    /// </summary>
    public static class AtlasBaker
    {
        /// <summary>
        /// Bake all pack results into atlases, one atlas per texture kind per queue.
        /// 把所有装箱结果烘焙为图集，每个队列每种贴图类别一个图集。
        /// </summary>
        public static List<ATOAtlas> Bake(ATOBuildContext bc, ATOAnalysisResult result, List<ATOPackResult> packResults)
        {
            var atlases = new List<ATOAtlas>();
            int index = 0;
            foreach (var pr in packResults)
            {
                bc.ThrowIfCancelled();
                var kinds = PresentKinds(pr);
                foreach (var kind in kinds)
                {
                    var atlas = BakeOne(bc, result, pr, kind, index++);
                    if (atlas != null) atlases.Add(atlas);
                }
            }
            return atlases;
        }

        private static List<ATOTextureKind> PresentKinds(ATOPackResult pr)
        {
            var kinds = new List<ATOTextureKind>();
            var seen = new HashSet<ATOTextureKind>();
            foreach (var g in pr.units)
            foreach (var u in g.usages)
            {
                var kind = NormalizeKind(u.kind);
                if (seen.Add(kind)) kinds.Add(kind);
            }
            return kinds;
        }

        private static ATOTextureKind NormalizeKind(ATOTextureKind kind) => ATOKindUtil.Normalize(kind);

        private static ATOAtlas BakeOne(ATOBuildContext bc, ATOAnalysisResult result, ATOPackResult pr, ATOTextureKind kind, int index)
        {
            int size = pr.size;
            var pixels = new Color32[size * size];
            var filled = new bool[size * size];
            bool transparent = false;

            var sources = new List<Texture2D>();
            var sourceSet = new HashSet<Texture2D>();

            foreach (var placed in pr.layout)
            {
                var usage = FindUsage(placed.island, pr, kind);
                if (usage == null || usage.texture == null) continue;
                if (sourceSet.Add(usage.texture)) sources.Add(usage.texture);

                if (kind == ATOTextureKind.Color && HasAlpha(usage))
                    transparent = true;

                BakeIsland(bc, usage, placed, pixels, filled, size);
            }

            if (sources.Count == 0) return null;

            // Pull-push fill. pull-push 填充。
            PullPush.Fill(pixels, filled, size, size, kind, transparent);

            // The atlas is "transparent" if ANY pixel has alpha < 255 (spec: distinguish by the
            // atlas' actual alpha channel). 图集实际含 alpha（任一像素 alpha<255）即视为透明（规范：按实际 alpha 通道区分）。
            transparent = AnyAlpha(pixels);

            var atlas = new ATOAtlas
            {
                name = $"ATO_{kind}_{index}",
                kind = kind,
                size = size,
                npot = result.settings.npotAtlas,
                packed = pr.layout,
                units = pr.units,
                sources = sources,
                utilization = ComputeUtilization(pr.layout, size),
                transparent = transparent,
            };

            atlas.texture = CreateTexture(atlas, pixels, result.settings);
            return atlas;
        }

        private static bool AnyAlpha(Color32[] pixels)
        {
            foreach (var p in pixels)
                if (p.a < 255) return true;
            return false;
        }

        private static bool HasAlpha(ATOTextureUsage usage)
        {
            if (usage.material == null) return false;
            return AlphaModeDetector.Detect(usage.material) != ATOAlphaMode.Opaque;
        }

        private static ATOTextureUsage FindUsage(ATOIsland island, ATOPackResult pr, ATOTextureKind kind)
        {
            foreach (var g in pr.units)
            {
                if (!g.islands.Contains(island)) continue;
                foreach (var u in g.usages)
                {
                    if (NormalizeKind(u.kind) == kind) return u;
                }
            }
            return null;
        }

        private static void BakeIsland(ATOBuildContext bc, ATOTextureUsage usage, ATOPackedIsland placed, Color32[] pixels, bool[] filled, int atlasSize)
        {
            var tex = usage.texture;
            int srcW = tex.width, srcH = tex.height;
            int rx = Mathf.Clamp(Mathf.FloorToInt(placed.island.bounds.xMin * srcW), 0, srcW - 1);
            int ry = Mathf.Clamp(Mathf.FloorToInt(placed.island.bounds.yMin * srcH), 0, srcH - 1);
            int rw = Mathf.Clamp(Mathf.CeilToInt(placed.island.bounds.width * srcW), 1, srcW - rx);
            int rh = Mathf.Clamp(Mathf.CeilToInt(placed.island.bounds.height * srcH), 1, srcH - ry);

            bool srgb = ATOTextureIO.IsSRGB(tex);
            var src = UVIslandScaler.GetLinearRegion(bc, tex, rx, ry, rw, rh);

            int tw = placed.size.x, th = placed.size.y;
            Color[] resampled;
            if (usage.kind == ATOTextureKind.NormalMap)
            {
                resampled = ResampleNormal(src, rw, rh, tw, th);
            }
            else
            {
                resampled = QualityMath.AreaResample(src, rw, rh, tw, th);
            }

            // Rotate the block. 旋转块。
            resampled = RotateBlock(resampled, tw, th, placed.rotationSteps, out int rw2, out int rh2);
            tw = rw2; th = rh2;

            // Un-premultiply and encode. 反预乘并编码。
            for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                int ox = placed.offset.x + x;
                int oy = placed.offset.y + y;
                if (ox < 0 || oy < 0 || ox >= atlasSize || oy >= atlasSize) continue;
                var c = resampled[y * tw + x];
                Color32 enc = Encode(c, srgb, usage.kind);
                pixels[oy * atlasSize + ox] = enc;
                filled[oy * atlasSize + ox] = true;
            }
        }

        private static Color32 Encode(Color linearPremult, bool srgb, ATOTextureKind kind)
        {
            // Un-premultiply. 反预乘。
            float a = linearPremult.a;
            if (a > 1e-6f)
            {
                linearPremult = new Color(linearPremult.r / a, linearPremult.g / a, linearPremult.b / a, a);
            }
            else
            {
                linearPremult = new Color(0, 0, 0, 0);
            }

            if (srgb)
            {
                return new Color32(
                    (byte)Mathf.RoundToInt(QualityMath.LinearToSRgb(Clamp01(linearPremult.r)) * 255f),
                    (byte)Mathf.RoundToInt(QualityMath.LinearToSRgb(Clamp01(linearPremult.g)) * 255f),
                    (byte)Mathf.RoundToInt(QualityMath.LinearToSRgb(Clamp01(linearPremult.b)) * 255f),
                    (byte)Mathf.RoundToInt(Clamp01(linearPremult.a) * 255f));
            }
            return new Color32(
                (byte)Mathf.RoundToInt(Clamp01(linearPremult.r) * 255f),
                (byte)Mathf.RoundToInt(Clamp01(linearPremult.g) * 255f),
                (byte)Mathf.RoundToInt(Clamp01(linearPremult.b) * 255f),
                (byte)Mathf.RoundToInt(Clamp01(linearPremult.a) * 255f));
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static Color[] ResampleNormal(Color[] src, int w, int h, int tw, int th)
        {
            int n = src.Length;
            var vec = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                vec[i] = ATOTextureIO.DecodeNormal(new Color32(
                    (byte)Mathf.RoundToInt(Clamp01(src[i].r) * 255f),
                    (byte)Mathf.RoundToInt(Clamp01(src[i].g) * 255f),
                    (byte)Mathf.RoundToInt(Clamp01(src[i].b) * 255f),
                    (byte)Mathf.RoundToInt(Clamp01(src[i].a) * 255f)));
            }
            var resampled = new Color[tw * th];
            float sx = (float)w / tw, sy = (float)h / th;
            for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                float x0 = x * sx, x1 = (x + 1) * sx;
                float y0 = y * sy, y1 = (y + 1) * sy;
                int ix0 = Mathf.FloorToInt(x0), ix1 = Mathf.Min(w, Mathf.CeilToInt(x1));
                int iy0 = Mathf.FloorToInt(y0), iy1 = Mathf.Min(h, Mathf.CeilToInt(y1));
                var sum = Vector3.zero; float wsum = 0f;
                for (int iy = iy0; iy < iy1; iy++)
                for (int ix = ix0; ix < ix1; ix++)
                {
                    float ox = Mathf.Min(x1, ix + 1) - Mathf.Max(x0, ix);
                    float oy = Mathf.Min(y1, iy + 1) - Mathf.Max(y0, iy);
                    float wt = ox * oy;
                    sum += vec[iy * w + ix] * wt; wsum += wt;
                }
                var v = wsum > 1e-9f ? (sum / wsum).normalized : Vector3.up;
                var enc = ATOTextureIO.EncodeNormal(v);
                resampled[y * tw + x] = new Color(enc.r / 255f, enc.g / 255f, enc.b / 255f, enc.a / 255f);
            }
            return resampled;
        }

        private static Color[] RotateBlock(Color[] src, int w, int h, int steps, out int rw, out int rh)
        {
            steps = ((steps % 4) + 4) % 4;
            if (steps == 0) { rw = w; rh = h; return src; }
            bool quarter = steps == 1 || steps == 3;
            rw = quarter ? h : w;
            rh = quarter ? w : h;
            var dst = new Color[rw * rh];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int nx = 0, ny = 0;
                switch (steps)
                {
                    case 1: nx = h - 1 - y; ny = x; break; // 90 CW
                    case 2: nx = w - 1 - x; ny = h - 1 - y; break; // 180
                    case 3: nx = y; ny = w - 1 - x; break; // 270 CW
                }
                dst[ny * rw + nx] = src[y * w + x];
            }
            return dst;
        }

        private static float ComputeUtilization(List<ATOPackedIsland> layout, int size)
        {
            long used = 0;
            foreach (var p in layout) used += (long)p.size.x * p.size.y;
            return size > 0 ? (float)((double)used / ((double)size * size)) : 0f;
        }

        private static Texture2D CreateTexture(ATOAtlas atlas, Color32[] pixels, ATOEffectiveSettings settings)
        {
            var tex = new Texture2D(atlas.size, atlas.size, TextureFormat.RGBA32, false, false);
            tex.name = atlas.name;
            tex.SetPixels32(pixels);
            tex.Apply(false, false); // no mipmaps yet; settings applied later. 暂不生成 mipmap；稍后应用设置。
            tex.wrapMode = TextureWrapMode.Clamp; // forced. 强制 Clamp。
            return tex;
        }
    }
}
