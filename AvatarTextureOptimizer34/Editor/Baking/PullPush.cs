// AvatarTextureOptimizer - PullPush
// EN: GPU pull-push (Levin et al.) via compute shader; fallbacks: dilation blit passes, then CPU BFS fill.
// For transparent atlases the extended regions keep alpha 0 (spec).
// CN: GPU pull-push（Levin 等）经 compute shader；回退：扩张 blit 逐趟、再 CPU BFS 填充。
//     透明图集的扩展区域 alpha 保持 0（按需求）。
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    public static class PullPush
    {
        private static ComputeShader _compute;
        private static bool _computeSearched;

        /// <summary>EN: Finds the compute shader by name. / CN: 按名称查找 compute shader。</summary>
        public static ComputeShader FindCompute()
        {
            if (_computeSearched) return _compute;
            _computeSearched = true;
            var guids = AssetDatabase.FindAssets("ATOPullPush t:ComputeShader");
            if (guids.Length > 0) _compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(AssetDatabase.GUIDToAssetPath(guids[0]));
            return _compute;
        }

        /// <summary>EN: Extends island edges to fill atlas empty space. / CN: 外扩岛边缘以填满图集空白。</summary>
        public static void Execute(AtoBuildState state, RenderTexture atlas, PackedAtlas info,
            RenderTexturePool pool, Material blitMat, bool useGpu, bool transparent)
        {
            var compute = useGpu ? FindCompute() : null;
            if (compute != null)
            {
                try { RunCompute(compute, atlas, transparent); return; }
                catch (Exception e)
                {
                    AtoLog.Warn($"Pull-push compute failed, falling back: {e.Message}");
                }
            }
            if (blitMat != null)
            {
                RunDilationBlits(state, atlas, blitMat, transparent);
                return;
            }
            RunCpuFill(state, atlas, pool, transparent);
        }

        // ------------------------------------------------------------- GPU compute

        private static void RunCompute(ComputeShader compute, RenderTexture atlas, bool transparent)
        {
            int w = atlas.width, h = atlas.height;
            int levels = 1;
            while ((w >> levels) >= 2 && (h >> levels) >= 2 && levels < 8) levels++;

            // EN: Ping-pong pyramids to avoid sampling & writing the same resource in one dispatch.
            // CN: 乒乓缓冲金字塔，避免同一资源在同一 dispatch 中被采样又写入。
            var a = new List<RenderTexture>();
            var b = new List<RenderTexture>();
            var in0 = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear);
            in0.enableRandomWrite = true;
            in0.Create();
            Graphics.Blit(atlas, in0);
            a.Add(in0);
            // EN: b[0] is a full-size ping buffer for the level-1 push.
            // CN: b[0] 为全尺寸乒乓缓冲，供第 1 层 push 使用。
            var b0 = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear);
            b0.enableRandomWrite = true;
            b0.Create();
            b.Add(b0);
            for (int l = 1; l < levels; l++)
            {
                int pw = Math.Max(2, w >> l), ph = Math.Max(2, h >> l);
                var ra = RenderTexture.GetTemporary(pw, ph, 0, RenderTextureFormat.ARGBFloat,
                    RenderTextureReadWrite.Linear);
                ra.enableRandomWrite = true;
                ra.Create();
                var rb = RenderTexture.GetTemporary(pw, ph, 0, RenderTextureFormat.ARGBFloat,
                    RenderTextureReadWrite.Linear);
                rb.enableRandomWrite = true;
                rb.Create();
                a.Add(ra);
                b.Add(rb);
            }

            // EN: Pull chain (downsample with alpha weighting).
            // CN: Pull 链（alpha 加权下采样）。
            int pull = compute.FindKernel("Pull");
            for (int l = 1; l < levels; l++)
            {
                int pw = a[l].width, ph = a[l].height;
                compute.SetTexture(pull, "InTex", a[l - 1]);
                compute.SetTexture(pull, "OutTex", a[l]);
                compute.SetInt("W", pw); compute.SetInt("H", ph);
                compute.Dispatch(pull, (pw + 7) / 8, (ph + 7) / 8, 1);
            }

            // EN: Push from the top down (ping-pong; final write into the atlas).
            // CN: 自顶向下 push（乒乓；最终写入图集）。
            int push = compute.FindKernel("Push");
            for (int l = levels - 1; l >= 1; l--)
            {
                RenderTexture dst = b[l - 1];
                RenderTexture src = a[l - 1];
                RenderTexture up = a[l];
                compute.SetTexture(push, "InTex", src);
                compute.SetTexture(push, "UpTex", up);
                compute.SetTexture(push, "OutTex", dst);
                compute.SetInt("W", dst.width); compute.SetInt("H", dst.height);
                compute.SetInt("UpW", up.width); compute.SetInt("UpH", up.height);
                compute.Dispatch(push, (dst.width + 7) / 8, (dst.height + 7) / 8, 1);
                // EN: Swap for the next iteration (the pushed result becomes the next iteration's source).
                // CN: 交换供下一轮使用（push 结果成为下一轮的源）。
                (a[l - 1], b[l - 1]) = (b[l - 1], a[l - 1]);
            }
            // EN: After the level-1 iteration, a[0] holds the fully pushed result.
            // CN: 第 1 层迭代后，a[0] 保存完整 push 结果。
            Graphics.Blit(a[0], atlas);

            if (transparent)
            {
                // EN: Force alpha 0 in empty regions (spec: 透明贴图 alpha 保持 0).
                // CN: 空白区域强制 alpha 0（按需求）。
                int zero = compute.FindKernel("ZeroEmptyAlpha");
                compute.SetTexture(zero, "OutTex", atlas);
                compute.SetInt("W", atlas.width); compute.SetInt("H", atlas.height);
                compute.Dispatch(zero, (atlas.width + 7) / 8, (atlas.height + 7) / 8, 1);
            }

            foreach (var rt in a) RenderTexture.ReleaseTemporary(rt);
            foreach (var rt in b) RenderTexture.ReleaseTemporary(rt);
        }

        // ------------------------------------------------------------- blit 扩张

        /// <summary>EN: Repeated 3x3 dilation blits with doubling radius (enough for padding gaps). / CN: 半径倍增的 3x3 扩张 blit 逐趟（足以覆盖 padding 间隙）。</summary>
        private static void RunDilationBlits(AtoBuildState state, RenderTexture atlas, Material blitMat,
            bool transparent)
        {
            blitMat.SetFloat("_Rotate", 0);
            int passes = 7; // 1+2+4+8+16+32+64 = 127px
            // EN: Ping-pong between two buffers (no same-texture read/write feedback).
            // CN: 双缓冲乒乓（避免同纹理读写反馈）。
            var ping = RenderTexture.GetTemporary(atlas.width, atlas.height, 0, atlas.format);
            var pong = RenderTexture.GetTemporary(atlas.width, atlas.height, 0, atlas.format);
            RenderTexture prev = RenderTexture.active;
            RenderTexture src = atlas, dst = ping;
            for (int i = 0; i < passes && !state.Cancelled; i++)
            {
                int radius = 1 << Math.Min(i, 6);
                blitMat.SetInt("_DilateRadius", radius);
                Graphics.Blit(src, dst, blitMat, 4);
                (src, dst) = (dst, src);
            }
            if (src != atlas) Graphics.Blit(src, atlas);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(ping);
            RenderTexture.ReleaseTemporary(pong);
        }

        // ------------------------------------------------------------- CPU 填充

        /// <summary>
        /// EN: CPU BFS fill at 4px granularity (final fallback). Reads the atlas, fills empty cells from the
        /// nearest content cell within the search radius, writes back.
        /// CN: CPU BFS 填充（最终回退）。读取图集，从搜索半径内最近的内容单元填充空单元，再写回。
        /// </summary>
        private static void RunCpuFill(AtoBuildState state, RenderTexture atlas, RenderTexturePool pool,
            bool transparent)
        {
            int w = atlas.width, h = atlas.height;
            int cw = (w + 3) / 4, ch = (h + 3) / 4;
            var prev = RenderTexture.active;
            RenderTexture.active = atlas;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            var px = tex.GetPixels32();
            int radius = 32; // 单元（128px）足够覆盖 padding 上限
            // EN: Multi-source BFS from content cells.
            // CN: 从内容单元出发的多源 BFS。
            var dist = new int[cw * ch];
            var queue = new Queue<int>();
            for (int i = 0; i < cw * ch; i++) dist[i] = -1;
            for (int cy = 0; cy < ch; cy++)
            {
                for (int cx = 0; cx < cw; cx++)
                {
                    // EN: Content = any non-zero alpha (or non-black for opaque atlases).
                    // CN: 内容 = 任意非零 alpha（不透明图集用非黑色判定）。
                    var c = px[(cy * 4 + 1) * w + (cx * 4 + 1)];
                    bool content = transparent ? c.a > 4 : (c.r | c.g | c.b | c.a) > 8;
                    if (content) { dist[cy * cw + cx] = 0; queue.Enqueue(cy * cw + cx); }
                }
            }
            var srcCell = new int[cw * ch];
            for (int i = 0; i < cw * ch; i++) srcCell[i] = -1;
            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                int cy = cell / cw, cx = cell % cw;
                int d = dist[cell];
                if (d >= radius) continue;
                int s = srcCell[cell] < 0 ? cell : srcCell[cell];
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
                        int ni = ny * cw + nx;
                        if (dist[ni] >= 0) continue;
                        dist[ni] = d + 1;
                        srcCell[ni] = s;
                        queue.Enqueue(ni);
                    }
                }
            }
            // EN: Fill empty cells from their source cell's center pixel.
            // CN: 从源单元中心像素填充空单元。
            for (int cy = 0; cy < ch; cy++)
            {
                for (int cx = 0; cx < cw; cx++)
                {
                    int cell = cy * cw + cx;
                    if (dist[cell] > 0 && srcCell[cell] >= 0)
                    {
                        int sy = srcCell[cell] / cw, sx = srcCell[cell] % cw;
                        var col = px[(sy * 4 + 1) * w + (sx * 4 + 1)];
                        if (transparent) col.a = 0;
                        for (int y = 0; y < 4; y++)
                            for (int x = 0; x < 4; x++)
                                px[(cy * 4 + y) * w + (cx * 4 + x)] = col;
                    }
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            Graphics.Blit(tex, atlas);
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }
}
