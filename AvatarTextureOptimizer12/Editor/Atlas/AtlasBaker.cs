// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Atlas baking, edge extension and UV remapping.
// AvatarTextureOptimizer (ATO) - 图集烘焙、边缘外扩与 UV 重映射。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Net.Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.MeshOps;
using Net.Fosa.AvatarTextureOptimizer.Editor.Quality;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>
    /// EN: Turns an <see cref="AtlasPlan"/> into an actual texture and produces the UV remap that the mesh
    ///     writer applies. All resampling happens in linear space with premultiplied alpha, matching the
    ///     quality evaluation exactly, so what we measured is what we ship.
    /// ZH: 把 <see cref="AtlasPlan"/> 变成真正的贴图，并生成网格写入阶段要用的 UV 重映射。
    ///     所有重采样都在线性空间、预乘 alpha 下进行，与质量评估完全一致，
    ///     因此“我们测量的”就是“我们交付的”。
    /// </summary>
    public static class AtlasBaker
    {
        /// <summary>
        /// EN: Bake one atlas. Returns an uncompressed RGBA32 texture; compression is applied later by
        ///     <see cref="TextureOutput"/> once the final format has been resolved and validated.
        /// ZH: 烘焙一个图集。返回未压缩的 RGBA32 贴图；压缩会在最终格式解析并校验之后
        ///     由 <see cref="TextureOutput"/> 施加。
        /// </summary>
        public static Texture2D Bake(AtlasPlan plan, ATOProgress progress)
        {
            int w = plan.Width, h = plan.Height;
            var accum = new float4[w * h];
            var coverage = new bool[w * h];

            int done = 0;
            foreach (var islandPlan in plan.Islands)
            {
                progress?.Report(done++, plan.Islands.Count, $"atlas #{plan.Index}");
                BlitIsland(islandPlan, accum, coverage, w, h);
            }

            // EN: Pull-push fill of the empty space, keeping alpha at 0 so transparent atlases stay transparent.
            // ZH: 对空白区域做 pull-push 填充，保持 alpha 为 0，使透明图集依然透明。
            PullPush.Fill(accum, coverage, w, h);

            var usage = FirstUsage(plan);
            bool srgb = usage != null && usage.SRGB && !usage.IsNormalMap;
            bool normal = usage != null && usage.IsNormalMap;
            bool dxt5nm = normal && NormalCodec.IsDxt5nm(usage.Content);
            bool premultiplied = usage != null && usage.AlphaMode != ATOAlphaMode.Opaque
                                              && usage.Content.HasAlpha && !normal;

            var pixels = new Color32[w * h];
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    var v = accum[i];

                    if (normal)
                    {
                        pixels[i] = NormalCodec.Encode(v.xyz, dxt5nm);
                        continue;
                    }

                    if (premultiplied)
                    {
                        float a = Mathf.Max(v.w, 1e-4f);
                        v = new float4(v.x / a, v.y / a, v.z / a, v.w);
                    }

                    if (srgb)
                    {
                        pixels[i] = new Color32(
                            ToByte(TextureIntrospection.LinearToSrgb(v.x)),
                            ToByte(TextureIntrospection.LinearToSrgb(v.y)),
                            ToByte(TextureIntrospection.LinearToSrgb(v.z)),
                            ToByte(v.w));
                    }
                    else
                    {
                        pixels[i] = new Color32(ToByte(v.x), ToByte(v.y), ToByte(v.z), ToByte(v.w));
                    }
                }
            });

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, /*linear:*/ !srgb)
            {
                name = $"ATO_Atlas_{plan.Index}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FirstFilterMode(plan),
                anisoLevel = MaxAniso(plan),
            };
            tex.SetPixels32(pixels);
            tex.Apply(true, false);

            ATOLog.Info($"baked {plan} padding={plan.Padding}px sRGB={srgb} normal={normal}");
            return tex;
        }

        private static byte ToByte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        private static TextureUsage FirstUsage(AtlasPlan plan)
        {
            foreach (var s in plan.Sources) return s;
            return null;
        }

        private static FilterMode FirstFilterMode(AtlasPlan plan)
        {
            // EN: Type groups already separate different filter modes, so any source is representative.
            // ZH: 类型组已经把不同 filterMode 分开，因此任取一个源即可代表整组。
            foreach (var s in plan.Sources) return s.Texture.filterMode;
            return FilterMode.Bilinear;
        }

        private static int MaxAniso(AtlasPlan plan)
        {
            int a = 1;
            foreach (var s in plan.Sources) a = Mathf.Max(a, s.Texture.anisoLevel);
            return a;
        }

        /// <summary>
        /// EN: Resample one island's source rect into its atlas slot, honouring the per-axis scale and the
        ///     90 degree rotation chosen by the packer.
        /// ZH: 把一个岛的源矩形重采样到它在图集中的位置，遵循装箱器选择的双轴缩放与 90 度旋转。
        /// </summary>
        private static void BlitIsland(IslandPlan plan, float4[] accum, bool[] coverage, int atlasW, int atlasH)
        {
            var usage = plan.Texture;
            var island = plan.Island;
            var src = TextureIntrospection.ReadStoredPixels(usage.Texture);
            if (!src.IsCreated) return;

            int texW = usage.Texture.width, texH = usage.Texture.height;
            var region = IslandScaler.ExtractIsland(usage, plan.SourceRect, src, texW, texH);

            // EN: The footprint comes from the UV group, so for a lower-resolution member it can be larger
            //     than this texture's own source rect. Downsampling is a box filter and upsampling is
            //     bilinear; pick per axis-pair rather than assuming one direction.
            // ZH: 占位来自 UV 组，因此对分辨率较低的成员而言它可能比该贴图自己的源矩形更大。
            //     下采样用 box，上采样用双线性；按实际方向选择，不做单向假设。
            var scaled = (island.ScaledWidth <= region.Width && island.ScaledHeight <= region.Height)
                ? region.Downsample(island.ScaledWidth, island.ScaledHeight)
                : region.UpsampleTo(island.ScaledWidth, island.ScaledHeight);

            int dw = island.Rotated ? scaled.Height : scaled.Width;
            int dh = island.Rotated ? scaled.Width : scaled.Height;

            for (int y = 0; y < dh; y++)
            {
                int ay = island.AtlasOrigin.y + y;
                if ((uint)ay >= (uint)atlasH) continue;

                for (int x = 0; x < dw; x++)
                {
                    int ax = island.AtlasOrigin.x + x;
                    if ((uint)ax >= (uint)atlasW) continue;

                    // EN: Rotation by 90 degrees CCW: dst(x,y) <- src(y, srcH-1-x).
                    // ZH: 逆时针旋转 90 度：dst(x,y) <- src(y, srcH-1-x)。
                    int sx = island.Rotated ? y : x;
                    int sy = island.Rotated ? scaled.Height - 1 - x : y;
                    sx = Mathf.Clamp(sx, 0, scaled.Width - 1);
                    sy = Mathf.Clamp(sy, 0, scaled.Height - 1);

                    int i = ay * atlasW + ax;
                    accum[i] = scaled[sx, sy];
                    coverage[i] = true;
                }
            }
        }

        /// <summary>
        /// EN: Compute the new UV for a mesh vertex that belongs to a placed island.
        /// ZH: 计算属于某个已放置岛的网格顶点的新 UV。
        /// </summary>
        public static Vector2 RemapUv(Vector2 originalUv, IslandPlan plan, int atlasW, int atlasH)
        {
            var island = plan.Island;

            // EN: Normalise into island-local [0,1].
            // ZH: 归一化到岛的局部 [0,1] 空间。
            float lx = originalUv.x - island.TileOffset.x - island.Min.x;
            float ly = originalUv.y - island.TileOffset.y - island.Min.y;

            float spanX = Mathf.Max(1e-8f, island.Max.x - island.Min.x);
            float spanY = Mathf.Max(1e-8f, island.Max.y - island.Min.y);
            float nx = lx / spanX;
            float ny = ly / spanY;

            float px, py;
            if (island.Rotated)
            {
                // EN: Must match the rotation used in BlitIsland.
                // ZH: 必须与 BlitIsland 中使用的旋转一致。
                px = island.AtlasOrigin.x + (1f - ny) * island.ScaledHeight;
                py = island.AtlasOrigin.y + nx * island.ScaledWidth;
            }
            else
            {
                px = island.AtlasOrigin.x + nx * island.ScaledWidth;
                py = island.AtlasOrigin.y + ny * island.ScaledHeight;
            }

            return new Vector2(px / atlasW, py / atlasH);
        }
    }

    /// <summary>
    /// EN: Pull-push (pyramid) hole filling. The classic Gortler et al. scheme: repeatedly downsample the
    ///     covered signal to build a pyramid ("pull"), then upsample back filling only uncovered texels
    ///     ("push"). This extends island edge colours infinitely outward, killing bleeding artefacts at
    ///     every mip level.
    /// ZH: Pull-push（金字塔）空洞填充。经典的 Gortler 等人方案：先反复下采样已覆盖信号构建金字塔（pull），
    ///     再逐级上采样、只填充未覆盖的纹素（push）。这会把岛的边缘颜色无限外扩，
    ///     在每一级 mip 上都能抑制渗色伪影。
    /// </summary>
    public static class PullPush
    {
        public static void Fill(float4[] color, bool[] coverage, int width, int height)
        {
            // EN: GPU first. The CPU pyramid below is the reference implementation and the fallback.
            // ZH: 优先走 GPU。下方的 CPU 金字塔既是参考实现也是兜底路径。
            if (Quality.GpuImageOps.TryPullPush(color, coverage, width, height)) return;

            var levels = new List<(float4[] c, float[] w, int w2, int h2)>();

            var w0 = new float[color.Length];
            var c0 = new float4[color.Length];
            for (int i = 0; i < color.Length; i++)
            {
                w0[i] = coverage[i] ? 1f : 0f;
                c0[i] = coverage[i] ? color[i] : float4.zero;
            }
            levels.Add((c0, w0, width, height));

            // ---- Pull ----
            while (levels[levels.Count - 1].w2 > 1 || levels[levels.Count - 1].h2 > 1)
            {
                var (pc, pw, cw, ch) = levels[levels.Count - 1];
                int nw = Mathf.Max(1, cw / 2), nh = Mathf.Max(1, ch / 2);
                var nc = new float4[nw * nh];
                var nwt = new float[nw * nh];

                Parallel.For(0, nh, y =>
                {
                    for (int x = 0; x < nw; x++)
                    {
                        float4 sum = float4.zero;
                        float wsum = 0f;
                        for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = Mathf.Min(cw - 1, x * 2 + dx);
                            int sy = Mathf.Min(ch - 1, y * 2 + dy);
                            int i = sy * cw + sx;
                            sum += pc[i];
                            wsum += pw[i];
                        }
                        int di = y * nw + x;
                        nc[di] = sum;
                        nwt[di] = wsum;
                    }
                });

                levels.Add((nc, nwt, nw, nh));
                if (nw == 1 && nh == 1) break;
            }

            // ---- Push ----
            for (int l = levels.Count - 1; l > 0; l--)
            {
                var (cc, cwt, cw, ch) = levels[l];
                var (fc, fwt, fw, fh) = levels[l - 1];

                Parallel.For(0, fh, y =>
                {
                    for (int x = 0; x < fw; x++)
                    {
                        int fi = y * fw + x;
                        if (fwt[fi] > 0f) continue;

                        int cx = Mathf.Min(cw - 1, x / 2);
                        int cy = Mathf.Min(ch - 1, y / 2);
                        int ci = cy * cw + cx;
                        if (cwt[ci] <= 0f) continue;

                        fc[fi] = cc[ci] / cwt[ci];
                        fwt[fi] = 1f;
                    }
                });
            }

            var (finalC, finalW, _, _) = levels[0];
            for (int i = 0; i < color.Length; i++)
            {
                if (coverage[i]) continue;
                if (finalW[i] <= 0f) { color[i] = float4.zero; continue; }

                var v = finalC[i];
                // EN: Keep alpha at zero outside the islands so transparent atlases remain transparent.
                // ZH: 岛之外保持 alpha 为 0，使透明图集依然透明。
                color[i] = new float4(v.x, v.y, v.z, 0f);
            }
        }
    }
}
