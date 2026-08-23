using System;
using System.Collections.Generic;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer.Editor.Apply;
using Net.Fosa.AvatarTextureOptimizer.Editor.Model;
using Net.Fosa.AvatarTextureOptimizer.Editor.Packing;
using UnityEngine;

internal static class PackingTests
{
    public static int Failures;

    private static void Assert(string name, bool ok, string detail = "")
    {
        if (!ok) Failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
    }

    private static UvIsland MakeIsland(int index, int x, int y, int w, int h, bool[] mask = null)
    {
        int cw = (w + 3) / 4, ch = (h + 3) / 4;
        mask ??= Enumerable.Repeat(true, cw * ch).ToArray();
        return new UvIsland
        {
            Index = index,
            Bounds = new RectInt(x, y, w, h),
            Mask = mask,
            MaskWidth = cw,
            MaskHeight = ch,
            CoveredCells = mask.Count(b => b),
            ScaledSize = new Vector2Int(w, h),
            Scale = Vector2.one,
        };
    }

    public static void Run()
    {
        Console.WriteLine("=== UV mapping round trip, unrotated ===");
        {
            var island = MakeIsland(0, 128, 64, 64, 32);
            island.AtlasOrigin = new Vector2Int(16, 8);
            island.Rotated = false;
            var reference = new Vector2Int(512, 256);
            var atlas = new Vector2Int(256, 128);

            // Island bottom-left corner in reference UV space.
            var uvMin = new Vector2(128f / 512f, 64f / 256f);
            var uvMax = new Vector2(192f / 512f, 96f / 256f);

            var a = AtlasUvMapping.MapToAtlas(uvMin, island, reference, atlas);
            var b = AtlasUvMapping.MapToAtlas(uvMax, island, reference, atlas);
            var rect = AtlasUvMapping.PlacedRect(island);

            Assert("min corner -> rect origin",
                Near(a.x * atlas.x, rect.x) && Near(a.y * atlas.y, rect.y),
                $"{a} -> ({a.x * atlas.x}, {a.y * atlas.y}) vs {rect}");
            Assert("max corner -> rect far corner",
                Near(b.x * atlas.x, rect.xMax) && Near(b.y * atlas.y, rect.yMax),
                $"{b} -> ({b.x * atlas.x}, {b.y * atlas.y}) vs {rect}");
        }

        Console.WriteLine();
        Console.WriteLine("=== UV mapping round trip, rotated 90 degrees ===");
        {
            var island = MakeIsland(0, 0, 0, 64, 32);
            island.AtlasOrigin = new Vector2Int(20, 40);
            island.Rotated = true;
            var reference = new Vector2Int(64, 32);
            var atlas = new Vector2Int(128, 128);

            var rect = AtlasUvMapping.PlacedRect(island);
            Assert("rotated rect swaps extents", rect.width == 32 && rect.height == 64, rect.ToString());

            // Every corner of the island must land on a corner of the placed rectangle, and the mapping
            // must be a bijection onto that rectangle.
            var corners = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            };
            var mapped = corners.Select(c => AtlasUvMapping.MapToAtlas(c, island, reference, atlas))
                .Select(v => new Vector2(v.x * atlas.x, v.y * atlas.y)).ToArray();

            bool allInside = mapped.All(p =>
                p.x >= rect.x - 0.001f && p.x <= rect.xMax + 0.001f &&
                p.y >= rect.y - 0.001f && p.y <= rect.yMax + 0.001f);
            Assert("rotated corners stay inside the placed rect", allInside,
                string.Join(" ", mapped.Select(p => p.ToString())));

            // The shader samples uv' = (v, 1-u): island local (0,0) must therefore appear at the
            // rectangle's TOP-left, i.e. offset (h, 0) with h being the island height.
            var atOrigin = mapped[0];
            Assert("island (0,0) maps to (origin.x + h, origin.y)",
                Near(atOrigin.x, rect.x + 32) && Near(atOrigin.y, rect.y),
                atOrigin.ToString());

            var atUmax = mapped[1]; // island (1,0)
            Assert("island (1,0) maps to (origin.x + h, origin.y + w)",
                Near(atUmax.x, rect.x + 32) && Near(atUmax.y, rect.y + 64),
                atUmax.ToString());

            var atBoth = mapped[2]; // island (1,1)
            Assert("island (1,1) maps to (origin.x, origin.y + w)",
                Near(atBoth.x, rect.x) && Near(atBoth.y, rect.y + 64),
                atBoth.ToString());

            // Area must be preserved.
            double area = Math.Abs(Cross(mapped[1] - mapped[0], mapped[3] - mapped[0]));
            Assert("rotated mapping preserves area", Near((float)area, 64 * 32, 0.01f), area.ToString("F2"));
        }

        Console.WriteLine();
        Console.WriteLine("=== Bitmask packer ===");
        {
            // Four 32x32 islands must tile a 64x64 atlas exactly at padding 0.
            var packer = new BitmaskPacker(16, 16); // 64/4
            var placed = 0;
            for (int i = 0; i < 4; i++)
            {
                var island = MakeIsland(i, 0, 0, 32, 32);
                var item = BitmaskPacker.BuildItem(island, 4, 0);
                if (packer.TryPlace(item, true)) placed++;
            }
            Assert("four 32x32 islands fit a 64x64 atlas", placed == 4, $"placed {placed}");
            Assert("packer reports full occupancy", packer.OccupiedCells == 16 * 16, packer.OccupiedCells.ToString());
        }
        {
            // Overfilling must fail rather than overlap.
            var packer = new BitmaskPacker(16, 16);
            int placed = 0;
            for (int i = 0; i < 6; i++)
            {
                var island = MakeIsland(i, 0, 0, 32, 32);
                if (packer.TryPlace(BitmaskPacker.BuildItem(island, 4, 0), true)) placed++;
            }
            Assert("packer refuses to overlap", placed == 4, $"placed {placed}");
        }
        {
            // Snapshot / restore must undo a speculative placement exactly.
            var packer = new BitmaskPacker(16, 16);
            var a = MakeIsland(0, 0, 0, 32, 32);
            packer.TryPlace(BitmaskPacker.BuildItem(a, 4, 0), true);
            var snap = packer.Snapshot();
            var b = MakeIsland(1, 0, 0, 32, 32);
            packer.TryPlace(BitmaskPacker.BuildItem(b, 4, 0), true);
            int afterSecond = packer.OccupiedCells;
            packer.Restore(snap);
            Assert("snapshot restores occupancy",
                packer.OccupiedCells == 64 && afterSecond == 128,
                $"after={afterSecond} restored={packer.OccupiedCells}");
        }
        {
            // EN: Shape packing must let a small island sit inside the concave hole of a big one. A
            //     rectangle packer could never do this: the L's bounding box already fills the atlas.
            // ZH: 形状装箱必须能让小岛放进大岛的凹槽里。矩形装箱永远做不到这一点：
            //     L 形的包围盒本身就已经占满了整张图集。
            // 8x8 cells = 32x32 px. L covers x<4 OR y<4; the hole is the 4x4 block at top-right.
            var lMask = new bool[8 * 8];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    lMask[y * 8 + x] = x < 4 || y < 4;
            var big = MakeIsland(0, 0, 0, 32, 32, lMask);

            var smallMask = Enumerable.Repeat(true, 4 * 4).ToArray();
            var small = MakeIsland(1, 0, 0, 16, 16, smallMask);

            var packer = new BitmaskPacker(8, 8);
            bool first = packer.TryPlace(BitmaskPacker.BuildItem(big, 4, 0), true);
            bool second = packer.TryPlace(BitmaskPacker.BuildItem(small, 4, 0), true);
            Assert("a small island nests into the concave hole of an L shape", first && second,
                $"first={first} second={second} origin={small.AtlasOrigin}");
            Assert("the nested island landed in the hole",
                small.AtlasOrigin.x == 4 && small.AtlasOrigin.y == 4, small.AtlasOrigin.ToString());
        }
        {
            // EN: Rotation must be attempted when the upright orientation does not fit.
            // ZH: 直立方向放不下时必须尝试旋转。
            // Atlas is 8 cells wide and 2 cells tall; the island is 2x8 and only fits rotated.
            var packer = new BitmaskPacker(8, 2);
            var tall = MakeIsland(0, 0, 0, 8, 32); // 2 x 8 cells
            bool placed = packer.TryPlace(BitmaskPacker.BuildItem(tall, 4, 0), true);
            Assert("a tall island is rotated to fit a wide atlas", placed && tall.Rotated,
                $"placed={placed} rotated={tall.Rotated}");

            var noRotation = new BitmaskPacker(8, 2);
            var tall2 = MakeIsland(0, 0, 0, 8, 32);
            bool placed2 = noRotation.TryPlace(BitmaskPacker.BuildItem(tall2, 4, 0), false);
            Assert("without rotation the same island does not fit", !placed2, $"placed={placed2}");
        }
        {
            // EN: Padding must actually separate islands. Two 16x16 px islands with a 4 px padding ring
            //     become 6x6 cell footprints, so their placements must be at least 6 cells apart on one
            //     axis - which is exactly what guarantees a 4 px gutter in the finished atlas.
            // ZH: padding 必须真正把岛隔开。两个 16x16 像素的岛加上 4 像素 padding 环后占 6x6 个单元，
            //     因此它们的放置位置在某一轴上至少相距 6 个单元——
            //     这正是最终图集中存在 4 像素间隙的保证。
            var packer = new BitmaskPacker(16, 16); // 64x64 px
            var a = MakeIsland(0, 0, 0, 16, 16);
            var b = MakeIsland(1, 0, 0, 16, 16);
            bool pa = packer.TryPlace(BitmaskPacker.BuildItem(a, 4, 4), true);
            bool pb = packer.TryPlace(BitmaskPacker.BuildItem(b, 4, 4), true);
            int dx = Math.Abs(a.AtlasOrigin.x - b.AtlasOrigin.x);
            int dy = Math.Abs(a.AtlasOrigin.y - b.AtlasOrigin.y);
            Assert("padded islands never touch", pa && pb && (dx >= 6 || dy >= 6),
                $"a={a.AtlasOrigin} b={b.AtlasOrigin} placed={pa}/{pb}");

            // EN: A padded island must not fit where an unpadded one would.
            // ZH: 加了 padding 的岛不应能放进未加 padding 时才放得下的位置。
            var tight = new BitmaskPacker(4, 4); // 16x16 px
            var exact = MakeIsland(0, 0, 0, 16, 16);
            Assert("an island exactly filling the atlas fits without padding",
                tight.TryPlace(BitmaskPacker.BuildItem(exact, 4, 0), true));
            var tight2 = new BitmaskPacker(4, 4);
            var padded = MakeIsland(0, 0, 0, 16, 16);
            Assert("the same island does not fit once padding is added",
                !tight2.TryPlace(BitmaskPacker.BuildItem(padded, 4, 4), true));
        }

        Console.WriteLine();
        Console.WriteLine("=== Candidate atlas pool ===");
        {
            var pot = AtlasCandidatePool.Build(1024, false);
            Assert("POT pool uses only powers of two",
                pot.All(c => IsPot(c.Width) && IsPot(c.Height)), $"{pot.Count} candidates");
            Assert("POT pool is ordered by area then squareness",
                IsOrdered(pot), "ordering");
            Assert("POT pool respects the maximum edge",
                pot.All(c => c.Width <= 1024 && c.Height <= 1024), "max edge");
            Assert("POT pool respects the minimum edge",
                pot.All(c => c.Width >= 64 && c.Height >= 64), "min edge");

            var npot = AtlasCandidatePool.Build(512, true);
            Assert("NPOT pool steps in multiples of four (block compression safe)",
                npot.All(c => c.Width % 4 == 0 && c.Height % 4 == 0), $"{npot.Count} candidates");
            Assert("NPOT pool steps in 64 texel increments",
                npot.All(c => c.Width % 64 == 0 && c.Height % 64 == 0), "step");

            Assert("padding is ceil(maxEdge/128) clamped up to the minimum",
                AtlasCandidatePool.PaddingFor(new AtlasCandidate(1024, 512), 4) == 8 &&
                AtlasCandidatePool.PaddingFor(new AtlasCandidate(256, 256), 4) == 4 &&
                AtlasCandidatePool.PaddingFor(new AtlasCandidate(256, 256), 16) == 16,
                $"{AtlasCandidatePool.PaddingFor(new AtlasCandidate(1024, 512), 4)}");
        }
    }

    private static bool IsOrdered(List<AtlasCandidate> list)
    {
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i - 1].Area > list[i].Area) return false;
            if (list[i - 1].Area == list[i].Area && list[i - 1].Aspect > list[i].Aspect + 1e-5f) return false;
        }
        return true;
    }

    private static bool IsPot(int v) => v > 0 && (v & (v - 1)) == 0;
    private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    private static bool Near(float a, float b, float tol = 0.001f) => Math.Abs(a - b) <= tol;
}
