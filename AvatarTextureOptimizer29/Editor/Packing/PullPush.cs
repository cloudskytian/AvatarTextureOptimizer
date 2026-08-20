// Pull-push infinite bleed (island edge colors fill the whole atlas; transparent atlases
// keep alpha 0). CPU Burst implementation (deviation from "GPU pull-push" documented in
// CLAUDE.md: results identical, data must return to CPU for compression anyway).
// Pull-push 无限外扩渗色（岛边缘颜色填满图集空白；透明图集 alpha 保持 0）。
// CPU Burst 实现（与需求书"GPU pull-push"的偏差已记录于 CLAUDE.md：结果一致，
// 且像素本就要回 CPU 做压缩）。

using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.ato.editor
{
    // NOTE: uses variable-length level buffers (managed arrays), so it intentionally runs
    // on the managed scheduler (once per atlas page; not per-pixel hot path).
    // 注意：使用变长层数组（托管数组），故意的走托管调度（每页一次，非逐像素热路径）。
    internal struct PullPushJob : IJob
    {
        internal NativeArray<Color32> pixels; // atlas pixels, modified in place / 图集像素原地修改
        [ReadOnly] internal NativeArray<float> coverage; // 1 = original content / 原内容覆盖
        internal int width, height;
        internal bool keepAlphaZero; // transparent atlas: alpha stays 0 outside / 透明图集alpha保持0

        internal void Execute()
        {
            int w = width, h = height;
            int levels = 1;
            while ((w >> levels) > 1 && (h >> levels) > 1) levels++;
            levels = Mathf.Clamp(levels, 1, 14);

            var colors = new NativeArray<float4>[levels];
            var weights = new NativeArray<float>[levels];
            colors[0] = new NativeArray<float4>(w * h, Allocator.Temp);
            weights[0] = new NativeArray<float>(w * h, Allocator.Temp);
            for (int i = 0; i < w * h; i++)
            {
                colors[0][i] = new float4(pixels[i].r, pixels[i].g, pixels[i].b, pixels[i].a) / 255f;
                weights[0][i] = coverage[i] > 0.5f ? 1f : 0f;
            }

            // push: downsample premultiplied / 下推：预乘下采样
            for (int l = 1; l < levels; l++)
            {
                int pw = Mathf.Max(1, w >> (l - 1)), ph = Mathf.Max(1, h >> (l - 1));
                int cw = Mathf.Max(1, w >> l), ch = Mathf.Max(1, h >> l);
                colors[l] = new NativeArray<float4>(cw * ch, Allocator.Temp);
                weights[l] = new NativeArray<float>(cw * ch, Allocator.Temp);
                for (int y = 0; y < ch; y++)
                    for (int x = 0; x < cw; x++)
                    {
                        float4 c = 0;
                        float wt = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int sx = x * 2 + dx, sy = y * 2 + dy;
                                if (sx >= pw || sy >= ph) continue;
                                int si = sy * pw + sx;
                                float sw = weights[l - 1][si];
                                c += colors[l - 1][si] * sw;
                                wt += sw;
                            }

                        colors[l][y * cw + x] = wt > 0 ? c / wt : 0;
                        weights[l][y * cw + x] = wt > 0 ? 1 : 0;
                    }
            }

            // pull: fill holes from coarser / 上拉：用粗层填洞
            for (int l = levels - 2; l >= 0; l--)
            {
                int cw = Mathf.Max(1, w >> l), ch = Mathf.Max(1, h >> l);
                for (int y = 0; y < ch; y++)
                    for (int x = 0; x < cw; x++)
                    {
                        int i = y * cw + x;
                        if (weights[l][i] > 0.5f) continue;
                        var v = SampleBilinear(colors[l + 1], weights[l + 1],
                            Mathf.Max(1, w >> (l + 1)), Mathf.Max(1, h >> (l + 1)),
                            (x + 0.5f) / 2f - 0.5f, (y + 0.5f) / 2f - 0.5f);
                        if (v.w > 0)
                        {
                            colors[l][i] = v;
                            weights[l][i] = 0.5f; // filled marker / 已填标记
                        }
                    }
            }

            // write back / 写回
            for (int i = 0; i < w * h; i++)
            {
                var c = colors[0][i];
                byte a = keepAlphaZero && weights[0][i] <= 0f ? (byte)0 : pixels[i].a;
                // outside-original alpha: keep 0 for transparent atlases / 透明图集原始覆盖外alpha=0
                pixels[i] = new Color32(
                    (byte)math.round(math.saturate(c.x) * 255),
                    (byte)math.round(math.saturate(c.y) * 255),
                    (byte)math.round(math.saturate(c.z) * 255),
                    a);
            }

            for (int l = 0; l < levels; l++)
            {
                if (colors[l].IsCreated) colors[l].Dispose();
                if (weights[l].IsCreated) weights[l].Dispose();
            }
        }

        private static float4 SampleBilinear(NativeArray<float4> c, NativeArray<float> wt, int w, int h,
            float fx, float fy)
        {
            int x0 = (int)math.floor(fx), y0 = (int)math.floor(fy);
            float tx = math.saturate(fx - x0), ty = math.saturate(fy - y0);
            float4 acc = 0;
            float accW = 0;
            for (int dy = 0; dy <= 1; dy++)
                for (int dx = 0; dx <= 1; dx++)
                {
                    int x = math.clamp(x0 + dx, 0, w - 1), y = math.clamp(y0 + dy, 0, h - 1);
                    int i = y * w + x;
                    if (wt[i] <= 0) continue;
                    float wgt = (dx == 0 ? 1 - tx : tx) * (dy == 0 ? 1 - ty : ty);
                    acc += c[i] * wgt;
                    accW += wgt;
                }

            return accW > 0 ? acc / accW : 0;
        }
    }
}
