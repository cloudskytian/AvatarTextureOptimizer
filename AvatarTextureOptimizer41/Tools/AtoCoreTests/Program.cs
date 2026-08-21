using System;
using System.Collections.Generic;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer.Pure;

// AtoCoreTests: standalone test harness for the pure C# cores (packing / quality math / island extraction).
// 纯 C# 核心（装箱 / 质量数学 / 岛提取）的独立单测。

namespace AtoCoreTests
{
    internal static class Program
    {
        private static int _passed, _failed;

        private static void Check(bool cond, string name)
        {
            if (cond) { _passed++; Console.WriteLine($"  PASS  {name}"); }
            else { _failed++; Console.WriteLine($"  FAIL  {name}"); }
        }

        private static void CheckNear(double a, double b, double eps, string name)
            => Check(Math.Abs(a - b) <= eps, $"{name} (got {a:F6}, want {b:F6})");

        private static int Main()
        {
            TestRaster();
            TestBLF();
            TestIslands();
            TestQualityMath();
            TestLayoutAssembly();
            Console.WriteLine($"\n== {_passed} passed, {_failed} failed ==");
            return _failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------ raster ------------------------------------------------
        private static void TestRaster()
        {
            Console.WriteLine("[raster]");
            // Full-quad quad: 2 triangles covering [0,1]x[0,1]. 全四边形。
            float[] uv = { 0, 0, 1, 0, 1, 1, 0, 1 };
            int[] tris = { 0, 1, 2, 0, 2, 3 };
            var m = AtoRaster.RasterizeTriangles(uv, tris, 0, 0, 1, 1, 64, 64);
            Check(m.PopCount() == m.WidthBlocks * m.HeightBlocks, "full quad fills all 16 blocks");
            Check(m.WidthBlocks == 16 && m.HeightBlocks == 16, "64px -> 16 blocks (4px granularity)");

            // Half quad: quad covers [0,0.25]x[0,1] inside a [0,0.5]x[0,1] region -> half the blocks filled.
            // 半四边形：区域 [0,0.5]x[0,1]，四边形覆盖 [0,0.25]x[0,1] → 约一半块被填充。
            float[] uv2 = { 0, 0, 0.25f, 0, 0.25f, 1, 0, 1 };
            int[] tris2 = { 0, 1, 2, 0, 2, 3 };
            var m2 = AtoRaster.RasterizeTriangles(uv2, tris2, 0, 0, 0.5f, 1, 64, 64);
            Check(m2.PopCount() >= 120 && m2.PopCount() <= 136, $"half quad ~128/256 blocks (got {m2.PopCount()})");

            // Rotation: 90° of full quad is still full. 旋转。
            var mr = m.Rotate90();
            Check(mr.PopCount() == m.PopCount(), "rotate90 preserves area");
        }

        // ------------------------------------------------ BLF ------------------------------------------------
        private static PackItem MakeItem(int wBlocks, int hBlocks, object tag)
        {
            var mask = new BitMask(wBlocks, hBlocks);
            for (int y = 0; y < hBlocks; y++)
                for (int x = 0; x < wBlocks; x++) mask.Set(x, y, true);
            return new PackItem { Mask = mask, Tag = tag };
        }

        private static void TestBLF()
        {
            Console.WriteLine("[blf]");
            // Trivially fits. 平凡可装。
            var items = new List<PackItem> { MakeItem(2, 2, "a"), MakeItem(3, 1, "b"), MakeItem(1, 4, "c") };
            var res = new List<Placement>();
            Check(AtoBLF.TryPack(items, 64, 64, 4, res), "simple pack succeeds");
            Check(ValidatePack(items, res, 64, 64, 4), "simple pack valid");

            // Guaranteed failure: single item bigger than atlas. 必然失败。
            var big = new List<PackItem> { MakeItem(40, 40, "big") };
            var res2 = new List<Placement>();
            Check(!AtoBLF.TryPack(big, 64, 64, 0, res2), "oversized item fails");

            // Shape-aware: L-shaped mask. 形状感知。
            var lmask = new BitMask(3, 3);
            lmask.Set(0, 0, true); lmask.Set(1, 0, true); lmask.Set(2, 0, true); lmask.Set(0, 1, true); lmask.Set(0, 2, true);
            var itemsL = new List<PackItem> { new PackItem { Mask = lmask, Tag = "L" } };
            var res3 = new List<Placement>();
            Check(AtoBLF.TryPack(itemsL, 64, 64, 0, res3), "L-shape packs");
            Check(ValidatePack(itemsL, res3, 64, 64, 0), "L-shape valid");

            // Fuzz: random masks, validate invariants every time. 模糊测试。
            var rng = new Random(12345);
            bool allOk = true;
            for (int iter = 0; iter < 200; iter++)
            {
                int aw = 16 + rng.Next(3) * 16, ah = 16 + rng.Next(3) * 16;
                var fz = new List<PackItem>();
                int n = 1 + rng.Next(8);
                for (int i = 0; i < n; i++)
                {
                    int w = 1 + rng.Next(6), h = 1 + rng.Next(6);
                    var mask = new BitMask(w, h);
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            if (rng.Next(100) < 75) mask.Set(x, y, true);
                    if (!mask.AnySet) mask.Set(0, 0, true);
                    fz.Add(new PackItem { Mask = mask, Tag = $"i{iter}_{i}" });
                }
                int pad = rng.Next(2) * 8;
                var fr = new List<Placement>();
                bool ok = AtoBLF.TryPack(fz, aw, ah, pad, fr);
                if (ok && !ValidatePack(fz, fr, aw, ah, pad)) { allOk = false; Console.WriteLine($"    fuzz iter {iter}: invalid placements"); break; }
            }
            Check(allOk, "fuzz 200 iterations valid");
        }

        private static bool ValidatePack(List<PackItem> items, List<Placement> placements, int atlasW, int atlasH, int padPx)
        {
            if (placements.Count != items.Count) return false;
            int padB = Math.Max(0, (padPx + 3) / 4);
            int aw = atlasW / 4, ah = atlasH / 4;

            // Shape-level occupancy: mark every mask block at its placement. 形状级占用：标记每个掩码块。
            var occ = new byte[aw * ah];
            var byTag = new Dictionary<object, PackItem>();
            foreach (var it in items) byTag[it.Tag] = it;

            foreach (var p in placements)
            {
                if (!byTag.TryGetValue(p.Tag, out var item)) return false;
                var mask = p.Rotated ? item.Mask.Rotate90() : item.Mask;
                int bx = p.X / 4, by = p.Y / 4;
                if (bx < 0 || by < 0 || bx + mask.WidthBlocks > aw || by + mask.HeightBlocks > ah) return false;
                for (int y = 0; y < mask.HeightBlocks; y++)
                    for (int x = 0; x < mask.WidthBlocks; x++)
                        if (mask.Get(x, y)) occ[(by + y) * aw + (bx + x)]++;
            }
            if (occ.Any(c => c > 1)) return false; // overlapping masks. 掩码重叠。

            // Independent invariant: for every pair, the rect distance between any two mask blocks must be > padB
            // (i.e. |ax-bx|>padB or |ay-by|>padB), which is exactly the content spacing the packer enforces.
            // 独立不变量：任意两掩码块的矩形距离须 > padB（|ax-bx|>padB 或 |ay-by|>padB），即装箱器实际强制的内容间距。
            var placedMasks = new List<(BitMask mask, int bx, int by)>();
            foreach (var p in placements)
                placedMasks.Add((p.Rotated ? byTag[p.Tag].Mask.Rotate90() : byTag[p.Tag].Mask, p.X / 4, p.Y / 4));

            for (int i = 0; i < placedMasks.Count; i++)
                for (int j = i + 1; j < placedMasks.Count; j++)
                {
                    var a = placedMasks[i]; var b = placedMasks[j];
                    for (int ay = 0; ay < a.mask.HeightBlocks; ay++)
                        for (int ax = 0; ax < a.mask.WidthBlocks; ax++)
                        {
                            if (!a.mask.Get(ax, ay)) continue;
                            int gax = a.bx + ax, gay = a.by + ay;
                            for (int by = 0; by < b.mask.HeightBlocks; by++)
                                for (int bx = 0; bx < b.mask.WidthBlocks; bx++)
                                {
                                    if (!b.mask.Get(bx, by)) continue;
                                    int gbx = b.bx + bx, gby = b.by + by;
                                    if (Math.Abs(gax - gbx) <= padB && Math.Abs(gay - gby) <= padB) return false;
                                }
                        }
                }
            return true;
        }

        // ------------------------------------------------ islands ------------------------------------------------
        private static void TestIslands()
        {
            Console.WriteLine("[islands]");
            // Two disjoint quads -> 2 islands. 两个不相交四边形 → 2 个岛。
            float[] uv = {
                0.0f, 0.0f, 0.1f, 0.0f, 0.1f, 0.1f, 0.0f, 0.1f,   // quad A. 四边形 A。
                0.5f, 0.5f, 0.6f, 0.5f, 0.6f, 0.6f, 0.5f, 0.6f,   // quad B. 四边形 B。
            };
            int[] tris = { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            var islands = IslandCore.Extract(uv, tris, 8);
            Check(islands.Count == 2, $"two disjoint quads -> 2 islands (got {islands.Count})");

            // Seam: duplicate vertex at the same UV must still connect. 接缝：同一 UV 的重复顶点仍应连通。
            float[] uvSeam = {
                0.0f, 0.0f, 0.5f, 0.0f, 0.5f, 0.5f, 0.0f, 0.5f,   // quad with left seam edge. 左接缝边。
                0.5f, 0.0f, 1.0f, 0.0f, 1.0f, 0.5f, 0.5f, 0.5f,   // right quad sharing the seam UVs. 共享接缝 UV 的右四边形。
            };
            int[] trisSeam = { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            var sIslands = IslandCore.Extract(uvSeam, trisSeam, 8);
            Check(sIslands.Count == 1, $"seam-shared vertices form one island (got {sIslands.Count})");

            // Wrap: triangle UV edge jump > 0.5. 跨缝：UV 边跳变 > 0.5。
            float[] uvWrap = { 0.95f, 0.0f, 0.05f, 0.0f, 0.05f, 0.1f };
            int[] trisWrap = { 0, 1, 2 };
            var wIslands = IslandCore.Extract(uvWrap, trisWrap, 3);
            Check(wIslands.Count == 1 && wIslands[0].CrossesWrap, "wrap-crossing triangle flagged CrossesWrap");

            // Out-of-bounds but translatable. 越界但可平移。
            float[] uvOob = { 1.2f, 0.3f, 1.3f, 0.3f, 1.3f, 0.4f, 1.2f, 0.4f };
            int[] trisOob = { 0, 1, 2, 0, 2, 3 };
            var oIslands = IslandCore.Extract(uvOob, trisOob, 4);
            Check(oIslands.Count == 1 && !oIslands[0].CrossesWrap, "OOB island is translatable");
            CheckNear(oIslands[0].TranslateU, -1.2, 1e-4, "OOB translate U");

            // Span > 1 -> whitelist. 跨度>1 → 白名单。
            float[] uvBig = { 0.0f, 0.0f, 1.2f, 0.0f, 1.2f, 0.1f, 0.0f, 0.1f };
            int[] trisBig = { 0, 1, 2, 0, 2, 3 };
            var bIslands = IslandCore.Extract(uvBig, trisBig, 4);
            Check(bIslands.Count == 1 && bIslands[0].CrossesWrap, "span>1 island flagged CrossesWrap");

            // Overlap merge. 重叠合并。
            float[] uvOv1 = { 0.0f, 0.0f, 0.5f, 0.0f, 0.5f, 0.5f, 0.0f, 0.5f };
            float[] uvOv2 = { 0.3f, 0.3f, 0.8f, 0.3f, 0.8f, 0.8f, 0.3f, 0.8f };
            var i1 = IslandCore.Extract(uvOv1, trisOob, 4)[0];
            var i2 = IslandCore.Extract(uvOv2, trisOob, 4)[0];
            var merged = IslandCore.MergeOverlapping(new List<Island> { i1, i2 });
            Check(merged.Count == 1, "overlapping islands merge into one");
        }

        // ------------------------------------------------ quality math ------------------------------------------------
        private static void TestQualityMath()
        {
            Console.WriteLine("[quality math]");
            // SSIM identical. SSIM 恒等。
            float[] a = new float[64 * 64], b = new float[64 * 64];
            var rng = new Random(7);
            for (int i = 0; i < a.Length; i++) { a[i] = (float)rng.NextDouble(); b[i] = a[i]; }
            CheckNear(QualityMath.SSIM(a, b, 64, 64), 1.0, 1e-9, "SSIM identical = 1");

            // SSIM black vs white is low. SSIM 黑白差异应低。
            float[] c = new float[64 * 64], d = new float[64 * 64];
            Array.Fill(c, 0f); Array.Fill(d, 1f);
            double s = QualityMath.SSIM(c, d, 64, 64);
            Check(s < 0.05, $"SSIM black-vs-white low (got {s:F4})");

            // MS-SSIM identical = 1; degraded < 1. MS-SSIM 恒等=1，退化<1。
            CheckNear(QualityMath.MSSSIM(a, b, 64, 64), 1.0, 1e-9, "MS-SSIM identical = 1");
            var degraded = (float[])a.Clone();
            for (int i = 0; i < degraded.Length; i += 3) degraded[i] = Math.Min(1f, degraded[i] + 0.2f);
            double ms = QualityMath.MSSSIM(a, degraded, 64, 64);
            Check(ms < 0.999, $"MS-SSIM degraded < 1 (got {ms:F6})");

            // CIEDE2000 Sharma test pairs. Sharma 测试对。
            CheckNear(QualityMath.DeltaE2000(50.0, 2.6772, -79.7751, 50.0, 0.0, -82.7485), 2.0425, 0.001, "dE00 pair1");
            CheckNear(QualityMath.DeltaE2000(50.0, 3.1571, -77.2803, 50.0, 0.0, -82.7485), 2.8615, 0.001, "dE00 pair2");
            CheckNear(QualityMath.DeltaE2000(50.0, 2.8361, -74.0200, 50.0, 0.0, -82.7485), 3.4412, 0.001, "dE00 pair3");
            CheckNear(QualityMath.DeltaE2000(50.0, 2.0, 0.0, 50.0, 2.0, 0.0), 0.0, 1e-9, "dE00 same color");

            // RGB->Lab sanity: white -> L≈100, black -> L≈0. RGB→Lab 合理性。
            QualityMath.RgbToLab(1, 1, 1, out float lw, out float aw, out float bw);
            QualityMath.RgbToLab(0, 0, 0, out float lb, out float ab, out float bb);
            CheckNear(lw, 100.0, 0.5, "Lab white L=100");
            CheckNear(aw, 0.0, 0.5, "Lab white a=0");
            CheckNear(lb, 0.0, 0.5, "Lab black L=0");

            // Alpha metrics. Alpha 指标。
            float[] al = { 0.2f, 0.7f, 0.5f, 0.9f, 1 };
            float[] bl = { 0.7f, 0.2f, 0.5f, 0.9f, 1 };
            // cutoff 0.5 (strict >): a -> {0,1,0,1,1}, b -> {1,0,0,1,1} -> inter 2, union 4 -> IoU = 0.5.
            // cutoff 0.5（严格大于）：inter=2, union=4 → IoU=0.5。
            CheckNear(QualityMath.CoverageIoU(al, bl, 5, 1, 0.5f), 0.5, 1e-6, "cutout IoU");
            CheckNear(QualityMath.AlphaRMSE(al, al, 5), 0.0, 1e-9, "alpha RMSE identical");

            // Normal angle: identical buffers -> 0°. 法线角度恒等 → 0°。
            float[] na = new float[6] { 0, 0, 1, 1, 0, 0 };
            float[] nb = new float[6] { 0, 0, 1, 1, 0, 0 };
            CheckNear(QualityMath.NormalAngleErrorP95(na, nb, 2), 0.0, 1e-9, "normal angle identical");
            float[] nc = new float[6] { 0, 0, 1, 0, 1, 0 };
            double ang = QualityMath.NormalAngleErrorP95(na, nc, 2);
            CheckNear(ang, 90.0, 0.5, $"normal angle 90° (got {ang:F2})");

            // Gray worst-channel RMSE. 灰度最差通道 RMSE。
            float[] g1 = { 1, 0, 1, 0, 1, 0, 1, 0 };
            float[] g2 = { 1, 1, 1, 1, 1, 1, 1, 1 };
            CheckNear(QualityMath.WorstChannelRMSE(g1, g2, 4, 2), 1.0, 1e-6, "worst channel RMSE");

            // Uniform detection. 纯色判定。
            float[] uni = new float[16];
            Array.Fill(uni, 0.5f);
            Check(QualityMath.IsUniform(uni, 4, 4), "uniform buffer detected");
            uni[0] = 0.9f;
            Check(!QualityMath.IsUniform(uni, 4, 4), "non-uniform detected");
        }

        // ------------------------------------------------ layout + assembly ------------------------------------------------
        private static void TestLayoutAssembly()
        {
            Console.WriteLine("[layout+assembly]");
            var candidates = AtoAtlasSizes.Candidates(4096, true);
            Check(candidates.Count == 7 && candidates[0] == 64 && candidates[^1] == 4096, "POT candidates 64..4096");
            var npot = AtoAtlasSizes.Candidates(4096, false);
            Check(npot.Count == (4096 - 64) / 64 + 1 && npot[1] == 128, "NPOT candidates step 64");

            // Three groups with a few islands each. 三个组，每组若干岛。
            var rng = new Random(99);
            var groups = new List<KeyValuePair<object, GroupLayout>>();
            var islandTags = new List<(object g, object i)>();
            var groupIslands = new Dictionary<object, List<PackItem>>();
            for (int g = 0; g < 3; g++)
            {
                var islands = new List<PackItem>();
                int ni = 2 + rng.Next(4);
                for (int i = 0; i < ni; i++)
                {
                    int w = 8 + rng.Next(8), h = 8 + rng.Next(8);
                    var mask = new BitMask(w, h);
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            if (rng.Next(100) < 85) mask.Set(x, y, true);
                    islands.Add(new PackItem { Mask = mask, Tag = (g, i) });
                    islandTags.Add((g, i));
                }
                groupIslands[g] = islands;
                var layout = AtoGroupLayout.Layout(islands, 4096, 16);
                Check(layout.Success, $"group {g} layout succeeds");
                groups.Add(new KeyValuePair<object, GroupLayout>(g, layout));
            }

            var atlases = AtoAtlasAssembly.Assemble(groups, candidates, 8);
            Check(atlases.Count >= 1, $"at least one atlas (got {atlases.Count})");

            // Every group appears in exactly one atlas. 每个组恰好出现在一张图集。
            var seenGroups = new HashSet<object>();
            foreach (var at in atlases)
                foreach (var tg in at.GroupTags) seenGroups.Add(tg);
            Check(seenGroups.Count == 3, $"all 3 groups placed (got {seenGroups.Count})");

            // Validate no overlaps when instantiating rects in each atlas. 验证实例化矩形不重叠。
            bool allOk = true;
            foreach (var at in atlases)
            {
                var rects = new List<(float x, float y, float w, float h)>();
                for (int gi = 0; gi < at.GroupTags.Count; gi++)
                {
                    var g = (int)at.GroupTags[gi];
                    var origin = at.GroupOriginsUV[gi];
                    foreach (var island in groupIslands[g])
                    {
                        var iso = (ValueTuple<int, int>)island.Tag;
                        var nr = groups[g].Value.IslandRects[island.Tag];
                        rects.Add((origin.x + nr.x, origin.y + nr.y, nr.w, nr.h));
                    }
                }
                for (int i = 0; i < rects.Count; i++)
                    for (int j = i + 1; j < rects.Count; j++)
                    {
                        var a = rects[i]; var b = rects[j];
                        bool overlap = a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
                        if (overlap) { allOk = false; }
                    }
            }
            Check(allOk, "no normalized rect overlaps in any atlas");

            // Oversized group falls back (dropped by Assemble). 超大组被装配省略（回退）。
            var hugeMask = new BitMask(1024, 1024); // 4096px square. 4096px 方块。
            for (int y = 0; y < 1024; y++) for (int x = 0; x < 1024; x++) hugeMask.Set(x, y, true);
            var hugeLayout0 = AtoGroupLayout.Layout(new List<PackItem> { new PackItem { Mask = hugeMask, Tag = "h" } }, 4096, 0);
            Check(hugeLayout0.Success, "huge group layout at 4096 (no padding)");
            var ats2 = AtoAtlasAssembly.Assemble(
                new List<KeyValuePair<object, GroupLayout>> { new KeyValuePair<object, GroupLayout>("h", hugeLayout0) },
                AtoAtlasSizes.Candidates(4096, true), 8);
            Check(ats2.Count == 0, "huge group cannot be assembled with padding -> fallback");
        }
    }
}
