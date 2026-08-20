using System;
using System.Threading.Tasks;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: Pull-push (Gortler et al. 1996) hole filling used to flood the empty space of an atlas with a
    ///     plausible extension of the nearest island colours.
    ///
    ///     Why this and not a fixed-radius dilate: a dilate only reaches N texels, so a mip level deeper
    ///     than N still samples uninitialised space and produces black seams. Pull-push builds a full
    ///     pyramid, so the extrapolation is effectively infinite and every mip level has valid colour
    ///     everywhere.
    ///
    ///     Alpha handling: the RGB channels are extrapolated but the alpha channel of empty space is
    ///     forced back to 0 afterwards, so a transparent atlas stays transparent while its RGB no longer
    ///     bleeds black into island edges.
    ///
    /// ZH: Pull-push（Gortler 等，1996）空洞填充，用邻近岛颜色的合理外推填满图集的空白区域。
    ///
    ///     为什么用它而不是固定半径膨胀：膨胀只能延伸 N 个纹素，
    ///     因此比 N 更深的 mip 层仍会采样到未初始化区域并产生黑缝。
    ///     Pull-push 构建完整金字塔，外推实际上是无限的，每一层 mip 处处都有有效颜色。
    ///
    ///     Alpha 处理：RGB 通道被外推，但空白区域的 alpha 事后被强制归零，
    ///     这样透明图集仍然透明，而其 RGB 不再向岛边缘渗黑。
    /// </summary>
    public static class PullPush
    {
        /// <summary>
        /// EN: Fill every texel whose <paramref name="valid"/> flag is false.
        /// ZH: 填充所有 <paramref name="valid"/> 标记为 false 的纹素。
        /// </summary>
        /// <param name="color">EN: RGBA buffer, modified in place. ZH: RGBA 缓冲，原地修改。</param>
        /// <param name="valid">EN: coverage flags, one per texel. ZH: 覆盖标记，每个纹素一个。</param>
        /// <param name="w">EN: width. ZH: 宽度。</param>
        /// <param name="h">EN: height. ZH: 高度。</param>
        /// <param name="keepAlphaZero">EN: force alpha of filled texels back to 0. ZH: 把被填充纹素的 alpha 强制归零。</param>
        public static void Fill(Color[] color, bool[] valid, int w, int h, bool keepAlphaZero)
        {
            // ---- Build the pyramid by pulling (weighted downsample of valid texels) -------------------
            var levels = new System.Collections.Generic.List<(Color[] c, float[] wgt, int w, int h)>();

            var c0 = new Color[w * h];
            var w0 = new float[w * h];
            Parallel.For(0, w * h, i =>
            {
                if (valid[i]) { c0[i] = color[i]; w0[i] = 1f; }
            });
            levels.Add((c0, w0, w, h));

            int cw = w, ch = h;
            while (cw > 1 || ch > 1)
            {
                int nw = Mathf.Max(1, cw / 2);
                int nh = Mathf.Max(1, ch / 2);
                var src = levels[levels.Count - 1];
                var nc = new Color[nw * nh];
                var nwgt = new float[nw * nh];

                Parallel.For(0, nh, y =>
                {
                    for (int x = 0; x < nw; x++)
                    {
                        float sw = 0;
                        Color acc = default;
                        for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = Mathf.Min(x * 2 + dx, src.w - 1);
                            int sy = Mathf.Min(y * 2 + dy, src.h - 1);
                            int si = sy * src.w + sx;
                            float ww = src.wgt[si];
                            if (ww <= 0f) continue;
                            acc.r += src.c[si].r * ww;
                            acc.g += src.c[si].g * ww;
                            acc.b += src.c[si].b * ww;
                            acc.a += src.c[si].a * ww;
                            sw += ww;
                        }
                        int di = y * nw + x;
                        if (sw > 0f)
                        {
                            nc[di] = new Color(acc.r / sw, acc.g / sw, acc.b / sw, acc.a / sw);
                            nwgt[di] = Mathf.Min(1f, sw * 0.25f);
                        }
                    }
                });

                levels.Add((nc, nwgt, nw, nh));
                cw = nw; ch = nh;
                if (nw == 1 && nh == 1) break;
            }

            // ---- Push back up, filling invalid texels from the coarser level ---------------------------
            for (int l = levels.Count - 2; l >= 0; l--)
            {
                var dst = levels[l];
                var src = levels[l + 1];
                Parallel.For(0, dst.h, y =>
                {
                    for (int x = 0; x < dst.w; x++)
                    {
                        int di = y * dst.w + x;
                        if (dst.wgt[di] >= 1f) continue;

                        int sx = Mathf.Min(x / 2, src.w - 1);
                        int sy = Mathf.Min(y / 2, src.h - 1);
                        int si = sy * src.w + sx;
                        if (src.wgt[si] <= 0f) continue;

                        float a = dst.wgt[di];
                        var coarse = src.c[si];
                        dst.c[di] = new Color(
                            dst.c[di].r * a + coarse.r * (1f - a),
                            dst.c[di].g * a + coarse.g * (1f - a),
                            dst.c[di].b * a + coarse.b * (1f - a),
                            dst.c[di].a * a + coarse.a * (1f - a));
                        dst.wgt[di] = Mathf.Max(a, src.wgt[si]);
                    }
                });
            }

            var final = levels[0];
            Parallel.For(0, w * h, i =>
            {
                if (valid[i]) return;
                var c = final.c[i];
                if (keepAlphaZero) c.a = 0f;
                color[i] = c;
            });
        }
    }
}
