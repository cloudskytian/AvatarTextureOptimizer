using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

namespace net.fosa.ato
{
    // ============================================================================
    // 颜色科学 / Color science: sRGB <-> linear, sRGB -> Lab (D65), CIEDE2000.
    // ============================================================================
    internal static class ATOColorMath
    {
        public static float SRGBToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        public static float LinearToSRGB(float c)
        {
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        /// <summary>sRGB(0..1) -> Lab (D65) / sRGB -> CIE Lab.</summary>
        public static void SRGBToLab(float r, float g, float b, out float L, out float a, out float bb)
        {
            float x = 0.4124564f * r + 0.3575761f * g + 0.1804375f * b;
            float y = 0.2126729f * r + 0.7151522f * g + 0.0721750f * b;
            float z = 0.0193339f * r + 0.1191920f * g + 0.9503041f * b;

            const float eps = 216f / 24389f;
            const float kappa = 24389f / 27f;

            float fx = F(x / 0.95047f, eps, kappa);
            float fy = F(y, eps, kappa);
            float fz = F(z / 1.08883f, eps, kappa);

            L = 116f * fy - 16f;
            a = 500f * (fx - fy);
            bb = 200f * (fy - fz);
        }

        private static float F(float t, float eps, float kappa)
        {
            return t > eps ? Mathf.Pow(t, 1f / 3f) : (kappa * t + 16f) / 116f;
        }

        /// <summary>CIEDE2000 ΔE / CIEDE2000 color difference.</summary>
        public static float DeltaE2000(float L1, float a1, float b1, float L2, float a2, float b2)
        {
            const float deg2rad = Mathf.PI / 180f;

            float C1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float C2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float Cbar = (C1 + C2) * 0.5f;
            float Cbar7 = Mathf.Pow(Cbar, 7);
            float G = 0.5f * (1f - Mathf.Sqrt(Cbar7 / (Cbar7 + Mathf.Pow(25, 7))));

            float a1p = (1f + G) * a1;
            float a2p = (1f + G) * a2;

            float C1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float C2p = Mathf.Sqrt(a2p * a2p + b2 * b2);

            float h1p = HueDeg(a1p, b1);
            float h2p = HueDeg(a2p, b2);

            float dLp = L2 - L1;
            float dCp = C2p - C1p;

            float dhp;
            if (C1p * C2p == 0) dhp = 0;
            else if (Mathf.Abs(h2p - h1p) <= 180) dhp = h2p - h1p;
            else if (h2p - h1p > 180) dhp = h2p - h1p - 360;
            else dhp = h2p - h1p + 360;

            float dHp = 2f * Mathf.Sqrt(C1p * C2p) * Mathf.Sin(dhp * 0.5f * deg2rad);

            float Lbar = (L1 + L2) * 0.5f;
            float Cbarp = (C1p + C2p) * 0.5f;

            float hbarp;
            if (C1p * C2p == 0) hbarp = h1p + h2p;
            else if (Mathf.Abs(h1p - h2p) <= 180) hbarp = (h1p + h2p) * 0.5f;
            else if (h1p + h2p < 360) hbarp = (h1p + h2p + 360) * 0.5f;
            else hbarp = (h1p + h2p - 360) * 0.5f;

            float T = 1f - 0.17f * Mathf.Cos((hbarp - 30) * deg2rad)
                          + 0.24f * Mathf.Cos(2 * hbarp * deg2rad)
                          + 0.32f * Mathf.Cos((3 * hbarp + 6) * deg2rad)
                          - 0.20f * Mathf.Cos((4 * hbarp - 63) * deg2rad);

            float dTheta = 30f * Mathf.Exp(-Mathf.Pow((hbarp - 275) / 25, 2));
            float Rc = 2f * Mathf.Sqrt(Cbar7 / (Cbar7 + Mathf.Pow(25, 7)));
            float Rt = -Mathf.Sin(2 * dTheta * deg2rad) * Rc;

            float Lm50sq = (Lbar - 50) * (Lbar - 50);
            float Sl = 1f + 0.015f * Lm50sq / Mathf.Sqrt(20 + Lm50sq);
            float Sc = 1f + 0.045f * Cbarp;
            float Sh = 1f + 0.015f * Cbarp * T;

            float dL = dLp / Sl, dC = dCp / Sc, dH = dHp / Sh;
            return Mathf.Sqrt(dL * dL + dC * dC + dH * dH + Rt * dC * dH);
        }

        private static float HueDeg(float a, float b)
        {
            if (a == 0 && b == 0) return 0;
            float h = Mathf.Atan2(b, a) * Mathf.Rad2Deg;
            return h < 0 ? h + 360 : h;
        }
    }

    // ============================================================================
    // 质量结果 / Quality evaluation result.
    // ============================================================================
    internal sealed class ATOQualityResult
    {
        public float msSsim = 1f;
        public bool ssimApplicable;
        public float de2000Mean = 0f;
        public bool deApplicable;
        public float alphaIoU = 1f;
        public bool ioUApplicable;
        public float alphaRmse = 0f;
        public bool rmseApplicable;
        public float normalAngleMean = 0f;
        public float normalAngleP95 = 0f;
        public bool normalApplicable;
        public float grayscaleRmse = 0f;
        public bool grayApplicable;

        /// <summary>相对阈值的最大超限比(≤1 表示全部达标) / Worst threshold ratio (≤1 means all pass).</summary>
        public float WorstRatio(ATOQualityParameters q)
        {
            float worst = 0f;
            if (ssimApplicable && q.msSsim > 0) worst = Mathf.Max(worst, msSsim / q.msSsim);
            if (deApplicable && q.deltaE2000 > 0) worst = Mathf.Max(worst, de2000Mean / q.deltaE2000);
            if (ioUApplicable && q.alphaIoU > 0) worst = Mathf.Max(worst, (1f - alphaIoU) / (1f - q.alphaIoU));
            if (rmseApplicable && q.alphaRmse > 0) worst = Mathf.Max(worst, alphaRmse / q.alphaRmse);
            if (normalApplicable)
            {
                if (q.normalAngleMean > 0) worst = Mathf.Max(worst, normalAngleMean / q.normalAngleMean);
                if (q.normalAngleP95 > 0) worst = Mathf.Max(worst, normalAngleP95 / q.normalAngleP95);
            }

            if (grayApplicable && q.grayscaleRmse > 0) worst = Mathf.Max(worst, grayscaleRmse / q.grayscaleRmse);
            return worst;
        }
    }

    /// <summary>评估上下文(类别/透明模式等) / Evaluation context (category, transparency modes, cutoffs).</summary>
    internal sealed class ATOEvalContext
    {
        public ATOTextureCategory category = ATOTextureCategory.Color;
        public bool hasAlpha;
        public bool cutout;             // 需要clip后IoU评估 / needs clipped-IoU evaluation
        public bool blend;              // 需要线性alpha RMSE评估 / needs linear alpha RMSE
        public bool renderModeAnimated; // 渲染模式被动画修改 -> 同时评估cutout与blend(最严苛) / render mode animated -> evaluate both
        public float[] cutoffs = { 0.5f };
        public int usedChannels = 0b1111; // 灰度贴图使用通道位掩码 / grayscale used-channel bitmask

        public bool normalMap => category == ATOTextureCategory.Normal;
        public bool grayscaleEval => category == ATOTextureCategory.Grayscale || category == ATOTextureCategory.Mask;
        public bool colorEval => category == ATOTextureCategory.Color;
    }

    // ============================================================================
    // 岛采样与质量评估 / Island sampling & quality evaluation.
    // 线性空间重采样, 透明贴图预乘alpha下采样, 将缩小后的覆盖区双线性上采样回原尺寸后与原图比较.
    // Linear-space resampling; transparent textures use premultiplied-alpha downsampling; the resized
    // coverage is bilinearly upsampled back to the original size and compared against the original.
    //
    // CPU 路径将比较分辨率限制在 512px(短边) 以内以保证速度; GPU(RenderTexture) 路径将全分辨率评估.
    // The CPU path caps the comparison resolution at 512px (short side) for speed; the GPU path will
    // evaluate at full resolution.
    // ============================================================================
    internal sealed class ATOIslandSample
    {
        public int w, h;            // 比较分辨率 / comparison resolution
        public float[] premulLin;   // 线性预乘 RGBA / linear premultiplied RGBA
        public float[] alpha;       // 原始alpha / original alpha
        public float[] srgbRef;     // 非预乘 sRGB 0..1 (用于ΔE) / non-premultiplied sRGB (for ΔE)
        public float[] normalVec;   // 解码重归一化法线 xyz / decoded renormalized normals
        public byte[] mask;         // 覆盖掩码 / coverage mask
        public ATOEvalContext ctx;

        private static float[] _gaussKernel;

        public static ATOIslandSample Create(ATOBuildState state, ATOIsland island, ATOTextureInfo tex, ATOIslandTexture it, ATOEvalContext ctx)
        {
            var readable = ATOTextureIO.EnsureReadable(tex);
            if (readable == null || it == null) return null;

            int rx = Mathf.Clamp(Mathf.FloorToInt(it.pixelRect.x), 0, readable.width - 1);
            int ry = Mathf.Clamp(Mathf.FloorToInt(it.pixelRect.y), 0, readable.height - 1);
            int pw = Mathf.Clamp(Mathf.CeilToInt(it.pixelRect.width), 1, readable.width - rx);
            int ph = Mathf.Clamp(Mathf.CeilToInt(it.pixelRect.height), 1, readable.height - ry);

            var px = ATOTextureIO.ReadRect(tex, new Rect(rx, ry, pw, ph));
            if (px == null || px.Length != pw * ph) return null;

            var s = new ATOIslandSample { ctx = ctx };

            // 分辨率上限: 短边 > 512 时整体缩小比较基准 / resolution cap: shrink the comparison baseline
            int scaleDown = 1;
            while (Mathf.Min(pw, ph) / scaleDown > 512) scaleDown *= 2;

            s.w = Mathf.Max(1, pw / scaleDown);
            s.h = Mathf.Max(1, ph / scaleDown);

            int n = s.w * s.h;
            s.premulLin = new float[n * 4];
            s.alpha = new float[n];
            s.srgbRef = new float[n * 4];
            s.mask = new byte[n];
            if (ctx.normalMap) s.normalVec = new float[n * 3];

            for (int y = 0; y < s.h; y++)
            {
                for (int x = 0; x < s.w; x++)
                {
                    // 箱式平均降采样原图 / box-average the original
                    float sr = 0, sg = 0, sb = 0, sa = 0;
                    int cnt = 0;
                    for (int dy = 0; dy < scaleDown; dy++)
                    {
                        for (int dx = 0; dx < scaleDown; dx++)
                        {
                            int sx = x * scaleDown + dx, sy = y * scaleDown + dy;
                            if (sx >= pw || sy >= ph) continue;
                            var c = px[sy * pw + sx];
                            sr += c.r; sg += c.g; sb += c.b; sa += c.a;
                            cnt++;
                        }
                    }

                    if (cnt == 0) continue;
                    sr /= cnt * 255f; sg /= cnt * 255f; sb /= cnt * 255f; sa /= cnt * 255f;

                    int idx = y * s.w + x;
                    s.alpha[idx] = sa;
                    s.srgbRef[idx * 4] = sr;
                    s.srgbRef[idx * 4 + 1] = sg;
                    s.srgbRef[idx * 4 + 2] = sb;
                    s.srgbRef[idx * 4 + 3] = sa;

                    float lr = ATOColorMath.SRGBToLinear(sr);
                    float lg = ATOColorMath.SRGBToLinear(sg);
                    float lb = ATOColorMath.SRGBToLinear(sb);
                    s.premulLin[idx * 4] = lr * sa;
                    s.premulLin[idx * 4 + 1] = lg * sa;
                    s.premulLin[idx * 4 + 2] = lb * sa;
                    s.premulLin[idx * 4 + 3] = sa;

                    if (ctx.normalMap)
                    {
                        float nx = lr * 2f - 1f, ny = lg * 2f - 1f;
                        float nz = Mathf.Sqrt(Mathf.Max(0f, 1f - nx * nx - ny * ny));
                        float len = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (len < 1e-6f) len = 1f;
                        s.normalVec[idx * 3] = nx / len;
                        s.normalVec[idx * 3 + 1] = ny / len;
                        s.normalVec[idx * 3 + 2] = nz / len;
                    }
                }
            }

            RasterizeMask(island, tex, it, s);
            return s;
        }

        private static void RasterizeMask(ATOIsland island, ATOTextureInfo tex, ATOIslandTexture it, ATOIslandSample s)
        {
            var uvList = island.owner.newUVs[island.channel];
            int[] tris = island.owner.mesh.triangles;
            var b = it.pixelRect;
            float stepX = b.width / (tex.width * s.w);
            float stepY = b.height / (tex.height * s.h);

            for (int y = 0; y < s.h; y++)
            {
                for (int x = 0; x < s.w; x++)
                {
                    // 比较坐标 -> 原贴图UV / comparison coords -> original UV
                    float u0 = b.x / tex.width + x * stepX;
                    float v0 = b.y / tex.height + y * stepY;
                    float u1 = u0 + stepX;
                    float v1 = v0 + stepY;

                    bool covered = false;
                    foreach (var t in island.triangles)
                    {
                        Vector2 a = uvList[tris[t * 3]];
                        Vector2 bb = uvList[tris[t * 3 + 1]];
                        Vector2 c = uvList[tris[t * 3 + 2]];
                        if (Mathf.Max(a.x, bb.x, c.x) < u0 || Mathf.Min(a.x, bb.x, c.x) > u1
                            || Mathf.Max(a.y, bb.y, c.y) < v0 || Mathf.Min(a.y, bb.y, c.y) > v1) continue;

                        if (PointInTri(new Vector2(u0, v0), a, bb, c) || PointInTri(new Vector2(u1, v0), a, bb, c)
                            || PointInTri(new Vector2(u0, v1), a, bb, c) || PointInTri(new Vector2(u1, v1), a, bb, c))
                        {
                            covered = true;
                            break;
                        }
                    }

                    if (covered) s.mask[y * s.w + x] = 1;
                }
            }
        }

        private static bool PointInTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d2 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            float d3 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        // ------------------------------------------------------------------
        /// <summary>评估指定缩放 / Evaluate at a given per-axis scale.</summary>
        public ATOQualityResult Evaluate(float sx, float sy)
        {
            var r = new ATOQualityResult();
            int tw = Mathf.Max(1, Mathf.RoundToInt(w * sx));
            int th = Mathf.Max(1, Mathf.RoundToInt(h * sy));

            if (tw == w && th == h)
            {
                // 未缩放 -> 全部完美 / no resizing -> perfect
                return r;
            }

            // 预乘线性下采样 / premultiplied linear downsampling
            var small = new float[tw * th * 4];
            var smallA = new float[tw * th];
            BilinearScale(premulLin, w, h, 4, small, tw, th);
            BilinearScale(alpha, w, h, 1, smallA, tw, th);

            // 上采样回原尺寸 / upsample back to the original size
            var up = new float[w * h * 4];
            var upA = new float[w * h];
            BilinearScale(small, tw, th, 4, up, w, h);
            BilinearScale(smallA, tw, th, 1, upA, w, h);

            int n = w * h;

            // 主色: MS-SSIM/SSIM (线性预乘空间, 掩码加权) / color: MS-SSIM/SSIM on premultiplied linear, mask-weighted
            if (ctx.colorEval)
            {
                int shortSide = Mathf.Min(w, h);
                if (shortSide >= 11)
                {
                    r.ssimApplicable = true;
                    r.msSsim = shortSide < 176
                        ? MaskedSSIM(premulLin, up, mask, w, h)
                        : MaskedMSSSIM(premulLin, up, mask, w, h);
                }
                // 短边 < 11px: 忽略此参数 / short side < 11px: parameter ignored

                // ΔE2000(可见像素, 非预乘sRGB) / ΔE2000 on visible pixels (non-premultiplied sRGB)
                r.deApplicable = true;
                float sum = 0;
                int cnt = 0;
                float cutoff = ctx.cutout ? MinCutoff() : 0f;
                for (int i = 0; i < n; i++)
                {
                    if (mask[i] == 0) continue;
                    float a0 = alpha[i], a1 = upA[i];
                    if (a0 <= cutoff || a1 <= cutoff) continue;
                    ATOColorMath.SRGBToLab(srgbRef[i * 4], srgbRef[i * 4 + 1], srgbRef[i * 4 + 2], out var L1, out var aa1, out var b1);
                    float ur = Mathf.Clamp01(up[i * 4] / Mathf.Max(a1, 1e-6f));
                    float ug = Mathf.Clamp01(up[i * 4 + 1] / Mathf.Max(a1, 1e-6f));
                    float ub = Mathf.Clamp01(up[i * 4 + 2] / Mathf.Max(a1, 1e-6f));
                    ATOColorMath.SRGBToLab(ATOColorMath.LinearToSRGB(ur), ATOColorMath.LinearToSRGB(ug), ATOColorMath.LinearToSRGB(ub), out var L2, out var aa2, out var b2);
                    sum += ATOColorMath.DeltaE2000(L1, aa1, b1, L2, aa2, b2);
                    cnt++;
                }

                r.de2000Mean = cnt > 0 ? sum / cnt : 0f;
            }

            // alpha 指标 / alpha metrics (cutout IoU / blend RMSE; 渲染模式动画时两者同时取最严苛)
            if (ctx.hasAlpha && (ctx.cutout || ctx.renderModeAnimated))
            {
                r.ioUApplicable = true;
                float best = 1f;
                foreach (var c in ctx.cutoffs)
                {
                    best = Mathf.Min(best, ClippedIoU(alpha, upA, mask, c));
                }

                r.alphaIoU = best;
            }

            if (ctx.hasAlpha && (ctx.blend || ctx.renderModeAnimated))
            {
                r.rmseApplicable = true;
                float sum = 0;
                int cnt = 0;
                for (int i = 0; i < n; i++)
                {
                    if (mask[i] == 0) continue;
                    float d = alpha[i] - upA[i];
                    sum += d * d;
                    cnt++;
                }

                r.alphaRmse = cnt > 0 ? Mathf.Sqrt(sum / cnt) : 0f;
            }

            // 法线: 解码重采样重归一化后角度误差(mean+p95) / normals: angle error after decode-resample-renormalize
            if (ctx.normalMap && normalVec != null)
            {
                r.normalApplicable = true;
                var smallN = new float[tw * th * 3];
                BilinearScale(normalVec, w, h, 3, smallN, tw, th);
                for (int i = 0; i < tw * th; i++)
                {
                    float l = Mathf.Sqrt(smallN[i * 3] * smallN[i * 3] + smallN[i * 3 + 1] * smallN[i * 3 + 1] + smallN[i * 3 + 2] * smallN[i * 3 + 2]);
                    if (l < 1e-6f) l = 1f;
                    smallN[i * 3] /= l; smallN[i * 3 + 1] /= l; smallN[i * 3 + 2] /= l;
                }

                var upN = new float[w * h * 3];
                BilinearScale(smallN, tw, th, 3, upN, w, h);

                var angles = new List<float>();
                for (int i = 0; i < n; i++)
                {
                    if (mask[i] == 0) continue;
                    float d = normalVec[i * 3] * upN[i * 3] + normalVec[i * 3 + 1] * upN[i * 3 + 1] + normalVec[i * 3 + 2] * upN[i * 3 + 2];
                    d = Mathf.Clamp(d, -1f, 1f);
                    angles.Add(Mathf.Acos(d) * Mathf.Rad2Deg);
                }

                if (angles.Count > 0)
                {
                    float sum = 0;
                    foreach (var v in angles) sum += v;
                    r.normalAngleMean = sum / angles.Count;
                    angles.Sort();
                    r.normalAngleP95 = angles[Mathf.Min(angles.Count - 1, Mathf.FloorToInt(angles.Count * 0.95f))];
                }
            }

            // 灰度/蒙版: 被使用通道线性RMSE逐通道取最差 / grayscale/mask: per-used-channel linear RMSE, worst wins
            if (ctx.grayscaleEval)
            {
                r.grayApplicable = true;
                float worst = 0;
                for (int ch = 0; ch < 4; ch++)
                {
                    if ((ctx.usedChannels & (1 << ch)) == 0) continue;
                    float sum = 0;
                    int cnt = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (mask[i] == 0) continue;
                        float d = premulLin[i * 4 + ch] - up[i * 4 + ch];
                        sum += d * d;
                        cnt++;
                    }

                    if (cnt > 0) worst = Mathf.Max(worst, Mathf.Sqrt(sum / cnt));
                }

                r.grayscaleRmse = worst;
            }

            return r;
        }

        private float MinCutoff()
        {
            float m = float.MaxValue;
            foreach (var c in ctx.cutoffs) m = Mathf.Min(m, c);
            return m == float.MaxValue ? 0.5f : m;
        }

        private static float ClippedIoU(float[] a, float[] b, byte[] mask, float cutoff)
        {
            float inter = 0, union = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (mask[i] == 0) continue;
                bool ca = a[i] >= cutoff, cb = b[i] >= cutoff;
                if (ca && cb) inter++;
                if (ca || cb) union++;
            }

            return union > 0 ? inter / union : 1f;
        }

        // ------------------------------------------------------------------
        /// <summary>双线性缩放(通道数可配置) / bilinear scaling (configurable channels).</summary>
        internal static void BilinearScale(float[] src, int sw, int sh, int ch, float[] dst, int dw, int dh)
        {
            float rx = sw / (float)dw;
            float ry = sh / (float)dh;
            for (int y = 0; y < dh; y++)
            {
                float fy = (y + 0.5f) * ry - 0.5f;
                int y0 = Mathf.FloorToInt(fy);
                float ty = fy - y0;
                int y0c = Mathf.Clamp(y0, 0, sh - 1);
                int y1c = Mathf.Clamp(y0 + 1, 0, sh - 1);
                for (int x = 0; x < dw; x++)
                {
                    float fx = (x + 0.5f) * rx - 0.5f;
                    int x0 = Mathf.FloorToInt(fx);
                    float tx = fx - x0;
                    int x0c = Mathf.Clamp(x0, 0, sw - 1);
                    int x1c = Mathf.Clamp(x0 + 1, 0, sw - 1);
                    for (int c = 0; c < ch; c++)
                    {
                        float v00 = src[(y0c * sw + x0c) * ch + c];
                        float v10 = src[(y0c * sw + x1c) * ch + c];
                        float v01 = src[(y1c * sw + x0c) * ch + c];
                        float v11 = src[(y1c * sw + x1c) * ch + c];
                        dst[(y * dw + x) * ch + c] = (v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty;
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // SSIM / MS-SSIM (掩码加权, 可分离高斯) / mask-weighted, separable Gaussian.
        // ------------------------------------------------------------------
        private static void GaussianKernel(out float[] k)
        {
            if (_gaussKernel == null)
            {
                _gaussKernel = new float[11];
                float sum = 0;
                for (int i = 0; i < 11; i++)
                {
                    float x = i - 5;
                    _gaussKernel[i] = Mathf.Exp(-(x * x) / (2 * 1.5f * 1.5f));
                    sum += _gaussKernel[i];
                }

                for (int i = 0; i < 11; i++) _gaussKernel[i] /= sum;
            }

            k = _gaussKernel;
        }

        /// <summary>可分离高斯卷积: dst = Σ k(t)·v(邻域), 跳过 mask=0 的像素 / separable Gaussian, skipping masked-out pixels.</summary>
        private static float[] ConvolveMasked(byte[] mask, float[] v, float[] k, int w, int h)
        {
            int n = w * h;
            var tmp = new float[n];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float acc = 0;
                    for (int t = 0; t < 11; t++)
                    {
                        int xx = Mathf.Clamp(x + t - 5, 0, w - 1);
                        if (mask[y * w + xx] != 0) acc += k[t] * v[y * w + xx];
                    }

                    tmp[y * w + x] = acc;
                }
            }

            var dst = new float[n];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float acc = 0;
                    for (int t = 0; t < 11; t++)
                    {
                        int yy = Mathf.Clamp(y + t - 5, 0, h - 1);
                        if (mask[yy * w + x] != 0) acc += k[t] * tmp[yy * w + x];
                    }

                    dst[y * w + x] = acc;
                }
            }

            return dst;
        }

        private static float MaskedSSIM(float[] a, float[] b, byte[] mask, int w, int h)
        {
            GaussianKernel(out var k);
            int n = w * h;
            var ones = new float[n];
            for (int i = 0; i < n; i++) ones[i] = 1f;
            var s1 = ConvolveMasked(mask, ones, k, w, h); // Σ w·m

            float c1 = 0.01f * 0.01f, c2 = 0.03f * 0.03f;
            double total = 0;
            int cnt = 0;

            for (int c = 0; c < 3; c++)
            {
                var ac = new float[n];
                var bc = new float[n];
                var a2 = new float[n];
                var b2 = new float[n];
                var ab = new float[n];
                for (int i = 0; i < n; i++)
                {
                    ac[i] = a[i * 4 + c];
                    bc[i] = b[i * 4 + c];
                    a2[i] = ac[i] * ac[i];
                    b2[i] = bc[i] * bc[i];
                    ab[i] = ac[i] * bc[i];
                }

                var sa = ConvolveMasked(mask, ac, k, w, h);
                var sb = ConvolveMasked(mask, bc, k, w, h);
                var saa = ConvolveMasked(mask, a2, k, w, h);
                var sbb = ConvolveMasked(mask, b2, k, w, h);
                var sab = ConvolveMasked(mask, ab, k, w, h);

                for (int i = 0; i < n; i++)
                {
                    if (mask[i] == 0 || s1[i] <= 1e-9f) continue;
                    float ux = sa[i] / s1[i], uy = sb[i] / s1[i];
                    float sxx = saa[i] / s1[i] - ux * ux;
                    float syy = sbb[i] / s1[i] - uy * uy;
                    float sxy = sab[i] / s1[i] - ux * uy;
                    total += (2 * ux * uy + c1) * (2 * sxy + c2) / ((ux * ux + uy * uy + c1) * (sxx + syy + c2));
                    cnt++;
                }
            }

            return cnt > 0 ? (float)(total / cnt) : 1f;
        }

        /// <summary>对比度+结构项(无亮度) / contrast+structure only (no luminance).</summary>
        private static float MaskedCSOnly(float[] a, float[] b, byte[] mask, int w, int h)
        {
            GaussianKernel(out var k);
            int n = w * h;
            var ones = new float[n];
            for (int i = 0; i < n; i++) ones[i] = 1f;
            var s1 = ConvolveMasked(mask, ones, k, w, h);

            float c2 = 0.03f * 0.03f;
            double total = 0;
            int cnt = 0;

            for (int c = 0; c < 3; c++)
            {
                var ac = new float[n];
                var bc = new float[n];
                var a2 = new float[n];
                var b2 = new float[n];
                var ab = new float[n];
                for (int i = 0; i < n; i++)
                {
                    ac[i] = a[i * 4 + c];
                    bc[i] = b[i * 4 + c];
                    a2[i] = ac[i] * ac[i];
                    b2[i] = bc[i] * bc[i];
                    ab[i] = ac[i] * bc[i];
                }

                var sa = ConvolveMasked(mask, ac, k, w, h);
                var sb = ConvolveMasked(mask, bc, k, w, h);
                var saa = ConvolveMasked(mask, a2, k, w, h);
                var sbb = ConvolveMasked(mask, b2, k, w, h);
                var sab = ConvolveMasked(mask, ab, k, w, h);

                for (int i = 0; i < n; i++)
                {
                    if (mask[i] == 0 || s1[i] <= 1e-9f) continue;
                    float ux = sa[i] / s1[i], uy = sb[i] / s1[i];
                    float sxx = saa[i] / s1[i] - ux * ux;
                    float syy = sbb[i] / s1[i] - uy * uy;
                    float sxy = sab[i] / s1[i] - ux * uy;
                    total += (2 * sxy + c2) / (sxx + syy + c2);
                    cnt++;
                }
            }

            return cnt > 0 ? (float)(total / cnt) : 1f;
        }

        /// <summary>MS-SSIM: 最多5尺度, 亮度项仅在最粗尺度 / up to 5 scales, luminance only at the coarsest.</summary>
        private static float MaskedMSSSIM(float[] a, float[] b, byte[] mask, int w, int h)
        {
            var weights = new[] { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
            var curA = a;
            var curB = b;
            var curM = mask;
            int cw = w, ch = h;
            double logResult = 0;
            float weightSum = 0f;

            for (int scale = 0; scale < 5 && Mathf.Min(cw, ch) >= 11; scale++)
            {
                bool last = scale == 4 || Mathf.Min(cw / 2, ch / 2) < 11;
                float ssim = last ? MaskedSSIM(curA, curB, curM, cw, ch) : MaskedCSOnly(curA, curB, curM, cw, ch);
                float wt = last ? weights[4] : weights[scale];
                logResult += wt * Mathf.Log(Mathf.Max(ssim, 1e-6f));
                weightSum += wt;
                if (last) break;

                curA = Downscale2(curA, cw, ch, 4);
                curB = Downscale2(curB, cw, ch, 4);
                curM = DownscaleMask2(curM, cw, ch);
                cw /= 2;
                ch /= 2;
            }

            return weightSum > 0 ? Mathf.Exp(logResult / weightSum) : 1f;
        }

        private static float[] Downscale2(float[] a, int w, int h, int ch)
        {
            int nw = Mathf.Max(1, w / 2), nh = Mathf.Max(1, h / 2);
            var r = new float[nw * nh * ch];
            for (int y = 0; y < nh; y++)
            {
                for (int x = 0; x < nw; x++)
                {
                    for (int c = 0; c < ch; c++)
                    {
                        int x0 = Mathf.Min(x * 2, w - 1), x1 = Mathf.Min(x * 2 + 1, w - 1);
                        int y0 = Mathf.Min(y * 2, h - 1), y1 = Mathf.Min(y * 2 + 1, h - 1);
                        float v = a[(y0 * w + x0) * ch + c] + a[(y0 * w + x1) * ch + c]
                                  + a[(y1 * w + x0) * ch + c] + a[(y1 * w + x1) * ch + c];
                        r[(y * nw + x) * ch + c] = v * 0.25f;
                    }
                }
            }

            return r;
        }

        private static byte[] DownscaleMask2(byte[] m, int w, int h)
        {
            int nw = Mathf.Max(1, w / 2), nh = Mathf.Max(1, h / 2);
            var r = new byte[nw * nh];
            for (int y = 0; y < nh; y++)
            {
                for (int x = 0; x < nw; x++)
                {
                    int x0 = Mathf.Min(x * 2, w - 1), x1 = Mathf.Min(x * 2 + 1, w - 1);
                    int y0 = Mathf.Min(y * 2, h - 1), y1 = Mathf.Min(y * 2 + 1, h - 1);
                    r[y * nw + x] = (byte)(m[y0 * w + x0] != 0 || m[y0 * w + x1] != 0 || m[y1 * w + x0] != 0 || m[y1 * w + x1] != 0 ? 1 : 0);
                }
            }

            return r;
        }
    }
}
