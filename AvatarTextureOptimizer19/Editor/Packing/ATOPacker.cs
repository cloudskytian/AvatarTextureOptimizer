// English: Burst-friendly bitmask BLF packer, candidate pool, 90° rotation, UV-group atomic units.
// 中文：位掩码 BLF 装箱、候选图集池、90° 旋转、UV 组原子装箱。
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal sealed class ATOMask
    {
        public int W;
        public int H;
        public ulong[] Bits; // row-major, 64-bit words

        public int Words
        {
            get { return (W + 63) >> 6; }
        }

        public static ATOMask Create(int w, int h)
        {
            var m = new ATOMask { W = w, H = h };
            m.Bits = new ulong[Mathf.Max(1, m.Words * h)];
            return m;
        }

        public ATOMask Clone()
        {
            var m = Create(W, H);
            Array.Copy(Bits, m.Bits, Bits.Length);
            return m;
        }

        public ATOMask Rotated90()
        {
            var r = Create(H, W);
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    if (Get(x, y)) r.Set(H - 1 - y, x, true);
                }
            }

            return r;
        }

        public bool Get(int x, int y)
        {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H) return false;
            var i = y * Words + (x >> 6);
            return (Bits[i] & (1UL << (x & 63))) != 0;
        }

        public void Set(int x, int y, bool v)
        {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;
            var i = y * Words + (x >> 6);
            var bit = 1UL << (x & 63);
            if (v) Bits[i] |= bit;
            else Bits[i] &= ~bit;
        }

        public int PopCount()
        {
            var n = 0;
            for (var i = 0; i < Bits.Length; i++) n += Pop(Bits[i]);
            return n;
        }

        private static int Pop(ulong x)
        {
            x = x - ((x >> 1) & 0x5555555555555555UL);
            x = (x & 0x3333333333333333UL) + ((x >> 2) & 0x3333333333333333UL);
            return (int)((((x + (x >> 4)) & 0x0F0F0F0F0F0F0F0FUL) * 0x0101010101010101UL) >> 56);
        }
    }

    internal sealed class ATOCandidate
    {
        public int W;
        public int H;
        public int Area { get { return W * H; } }
        public float Aspect
        {
            get
            {
                var a = Mathf.Max(W, H);
                var b = Mathf.Max(1, Mathf.Min(W, H));
                return a / (float)b;
            }
        }
    }

    internal static class ATOPacker
    {
        public const int Granularity = 4;

        public static void Pack(ATOState state)
        {
            if (!state.GenerateAtlases)
            {
                ATOWholeTexture.Scale(state);
                return;
            }

            var maxEdge = ResolveMaxEdge(state);
            var minPad = (int)state.Settings.minPadding;
            var pool = BuildPool(state.Settings.experimentalNpot, maxEdge);

            // Type-group queues: key = companions + linear + filter
            var queues = new Dictionary<string, List<ATOUvGroup>>();
            foreach (var g in state.UvGroups)
            {
                var key = g.Companions + "|" + g.Linear + "|" + g.Filter;
                List<ATOUvGroup> list;
                if (!queues.TryGetValue(key, out list))
                {
                    list = new List<ATOUvGroup>();
                    queues[key] = list;
                }

                list.Add(g);
            }

            foreach (var kv in queues)
            {
                kv.Value.Sort((a, b) => RasterArea(b).CompareTo(RasterArea(a)));
                PackQueue(state, kv.Value, pool, maxEdge, minPad);
            }
        }

        private static int ResolveMaxEdge(ATOState state)
        {
            if (state.Settings.maxAtlasEdgeOverride > 0) return state.Settings.maxAtlasEdgeOverride;
            return state.Platform == ATOBuildPlatform.PC ? 8192 : 4096;
        }

        private static List<ATOCandidate> BuildPool(bool npot, int maxEdge)
        {
            var list = new List<ATOCandidate>();
            if (npot)
            {
                // English: Full W×H cartesian product at 64px is thousands of candidates and not bake-safe.
                // We emit the area ladder with square-first plus 2:1 / 4:3 / 3:2, still 64-step and ≤ maxEdge.
                // 中文：64 步进的全笛卡尔积候选数量不适合烘焙。按面积阶梯生成正方形优先及 2:1 / 4:3 / 3:2。
                for (var s = 64; s <= maxEdge; s += 64)
                {
                    list.Add(new ATOCandidate { W = s, H = s });
                    var half = s / 2 / 64 * 64;
                    if (half >= 64)
                    {
                        list.Add(new ATOCandidate { W = s, H = half });
                        list.Add(new ATOCandidate { W = half, H = s });
                    }
                    var h43 = Mathf.Max(64, (s * 3 / 4) / 64 * 64);
                    if (h43 != s) list.Add(new ATOCandidate { W = s, H = h43 });
                    var w32 = Mathf.Max(64, (s * 2 / 3) / 64 * 64);
                    if (w32 != s) list.Add(new ATOCandidate { W = s, H = w32 });
                }
            }
            else
            {
                for (var w = 64; w <= maxEdge; w <<= 1)
                for (var h = 64; h <= maxEdge; h <<= 1)
                    list.Add(new ATOCandidate { W = w, H = h });
            }

            list.Sort((a, b) =>
            {
                var c = a.Area.CompareTo(b.Area);
                if (c != 0) return c;
                return a.Aspect.CompareTo(b.Aspect);
            });
            return list;
        }

        private static int RasterArea(ATOUvGroup g)
        {
            var a = 0;
            foreach (var isl in g.Islands)
            {
                var w = Mathf.Max(1, Mathf.CeilToInt(isl.PixelBounds.width * isl.Scale.x));
                var h = Mathf.Max(1, Mathf.CeilToInt(isl.PixelBounds.height * isl.Scale.y));
                a += w * h;
            }

            return a;
        }

        private static void PackQueue(ATOState state, List<ATOUvGroup> queue, List<ATOCandidate> pool, int maxEdge,
            int minPad)
        {
            var open = new List<AtlasBuilder>();
            foreach (var group in queue)
            {
                state.Progress.ThrowIfCanceled();
                if (state.SkipAtlasTextures.Overlaps(group.Textures))
                {
                    group.Abandoned = true;
                    state.Log.VerboseInfo("UV group #" + group.Id + " skip atlas (shared with whitelist UV)");
                    continue;
                }

                var masks = RasterizeUniqueUv(group);
                var need = 0;
                foreach (var m in masks) need += m.Mask.PopCount() * Granularity * Granularity;

                var placed = false;
                foreach (var b in open)
                {
                    if (TryPlace(b, group, masks, minPad))
                    {
                        placed = true;
                        break;
                    }
                }

                if (placed) continue;

                var fitted = false;
                foreach (var cand in pool)
                {
                    if (cand.Area < need) continue;
                    var nb = new AtlasBuilder(cand.W, cand.H);
                    if (!TryPlace(nb, group, masks, minPad)) continue;
                    open.Add(nb);
                    fitted = true;
                    break;
                }

                if (fitted) continue;

                group.Abandoned = true;
                var name = FirstName(group);
                state.Report.Warnings.Add("pack failed " + name);
                ErrorReport.ReportError(ATOLoc.L, ErrorSeverity.NonFatal, "warn.packFailed", name);
                state.Log.Warn("UV group #" + group.Id + " cannot fit max atlas, atlasing abandoned");
            }

            foreach (var b in open)
            {
                var atlases = ATOComposer.ComposeBySemantic(state, b);
                foreach (var atlas in atlases) state.Atlases.Add(atlas);
            }
        }

        private static string FirstName(ATOUvGroup g)
        {
            foreach (var t in g.Textures)
            {
                if (t != null) return t.name;
            }

            return "group#" + g.Id;
        }

        private sealed class UniqueUv
        {
            public ATOIsland Representative;
            public ATOMask Mask;
            public readonly List<ATOIsland> Siblings = new List<ATOIsland>();
        }

        private static string UvIdentity(ATOIsland isl)
        {
            var rid = isl.Renderer != null && isl.Renderer.Renderer != null
                ? isl.Renderer.Renderer.GetInstanceID()
                : 0;
            return rid + "|" + isl.UvChannel + "|" +
                   isl.UvBounds.xMin.ToString("F4") + "|" + isl.UvBounds.yMin.ToString("F4") + "|" +
                   isl.UvBounds.width.ToString("F4") + "|" + isl.UvBounds.height.ToString("F4");
        }

        private static List<UniqueUv> RasterizeUniqueUv(ATOUvGroup group)
        {
            var map = new Dictionary<string, UniqueUv>();
            foreach (var isl in group.Islands)
            {
                var key = UvIdentity(isl);
                UniqueUv u;
                if (!map.TryGetValue(key, out u))
                {
                    var pw = Mathf.Max(1, Mathf.CeilToInt(isl.PixelBounds.width * isl.Scale.x));
                    var ph = Mathf.Max(1, Mathf.CeilToInt(isl.PixelBounds.height * isl.Scale.y));
                    var gw = Mathf.Max(1, (pw + Granularity - 1) / Granularity);
                    var gh = Mathf.Max(1, (ph + Granularity - 1) / Granularity);
                    var mask = ATOMask.Create(gw, gh);
                    RasterizeIslandShape(isl, mask, pw, ph);
                    if (mask.PopCount() == 0)
                    {
                        for (var y = 0; y < gh; y++)
                        for (var x = 0; x < gw; x++)
                            mask.Set(x, y, true);
                    }

                    u = new UniqueUv { Representative = isl, Mask = mask };
                    map[key] = u;
                }
                else
                {
                    // Barrel: keep the largest scaled footprint so every sibling fits the slot.
                    u.Representative.Scale = new Vector2(
                        Mathf.Max(u.Representative.Scale.x, isl.Scale.x),
                        Mathf.Max(u.Representative.Scale.y, isl.Scale.y));
                }

                u.Siblings.Add(isl);
            }

            var list = new List<UniqueUv>(map.Values);
            list.Sort((a, b) =>
            {
                var c = b.Mask.PopCount().CompareTo(a.Mask.PopCount());
                if (c != 0) return c;
                return Mathf.Max(b.Mask.W, b.Mask.H).CompareTo(Mathf.Max(a.Mask.W, a.Mask.H));
            });
            return list;
        }

        private static bool TryPlace(AtlasBuilder builder, ATOUvGroup group, List<UniqueUv> uniques, int minPad)
        {
            var padCells = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(minPad, builder.W / 128f) / (float)Granularity));
            var snapshot = builder.Occupancy.Clone();
            var placements = new List<Place>();
            foreach (var item in uniques)
            {
                Place p;
                if (!builder.Find(item.Mask, padCells, out p))
                {
                    builder.Occupancy = snapshot;
                    return false;
                }

                builder.Stamp(p);
                p.Island = item.Representative;
                p.Siblings = item.Siblings;
                placements.Add(p);
            }

            foreach (var p in placements)
            {
                var packX = p.X * Granularity;
                var packY = p.Y * Granularity;
                var packW = (p.Rotated ? p.Mask.H : p.Mask.W) * Granularity;
                var packH = (p.Rotated ? p.Mask.W : p.Mask.H) * Granularity;
                foreach (var isl in p.Siblings)
                {
                    isl.PackX = packX;
                    isl.PackY = packY;
                    isl.PackW = packW;
                    isl.PackH = packH;
                    isl.Rotated = p.Rotated;
                    builder.Islands.Add(isl);
                }
            }

            foreach (var t in group.Textures) builder.Sources.Add(t);
            builder.Groups.Add(group);
            group.Packed = true;
            return true;
        }

        private static void RasterizeIslandShape(ATOIsland isl, ATOMask mask, int pw, int ph)
        {
            if (isl.Renderer == null || isl.Renderer.Mesh == null) return;
            var mesh = isl.Renderer.Mesh;
            var uvs = new List<Vector2>();
            mesh.GetUVs(isl.UvChannel, uvs);
            if (uvs.Count == 0) return;
            int[] tris;
            try { tris = mesh.GetTriangles(isl.Submesh, true); }
            catch { return; }

            var bx = isl.UvBounds.xMin;
            var by = isl.UvBounds.yMin;
            var bw = Mathf.Max(1e-8f, isl.UvBounds.width);
            var bh = Mathf.Max(1e-8f, isl.UvBounds.height);
            foreach (var t in isl.TriangleIndices)
            {
                if (t < 0 || t * 3 + 2 >= tris.Length) continue;
                var i0 = tris[t * 3];
                var i1 = tris[t * 3 + 1];
                var i2 = tris[t * 3 + 2];
                if (i0 >= uvs.Count || i1 >= uvs.Count || i2 >= uvs.Count) continue;
                var p0 = new Vector2((uvs[i0].x + isl.UvTranslate.x - bx) / bw * pw,
                    (uvs[i0].y + isl.UvTranslate.y - by) / bh * ph);
                var p1 = new Vector2((uvs[i1].x + isl.UvTranslate.x - bx) / bw * pw,
                    (uvs[i1].y + isl.UvTranslate.y - by) / bh * ph);
                var p2 = new Vector2((uvs[i2].x + isl.UvTranslate.x - bx) / bw * pw,
                    (uvs[i2].y + isl.UvTranslate.y - by) / bh * ph);
                FillTri(mask, p0, p1, p2);
            }
        }

        private static void FillTri(ATOMask mask, Vector2 a, Vector2 b, Vector2 c)
        {
            var minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) / Granularity), 0, mask.W - 1);
            var minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)) / Granularity), 0, mask.H - 1);
            var maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) / Granularity), 0, mask.W - 1);
            var maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)) / Granularity), 0, mask.H - 1);
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var p = new Vector2((x + 0.5f) * Granularity, (y + 0.5f) * Granularity);
                    if (PointInTri(p, a, b, c)) mask.Set(x, y, true);
                }
            }
        }

        private static bool PointInTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            var v0 = c - a;
            var v1 = b - a;
            var v2 = p - a;
            var dot00 = Vector2.Dot(v0, v0);
            var dot01 = Vector2.Dot(v0, v1);
            var dot02 = Vector2.Dot(v0, v2);
            var dot11 = Vector2.Dot(v1, v1);
            var dot12 = Vector2.Dot(v1, v2);
            var inv = dot00 * dot11 - dot01 * dot01;
            if (Mathf.Abs(inv) < 1e-12f) return true;
            var u = (dot11 * dot02 - dot01 * dot12) / inv;
            var v = (dot00 * dot12 - dot01 * dot02) / inv;
            return u >= -0.01f && v >= -0.01f && u + v <= 1.01f;
        }

        internal sealed class Place
        {
            public int X;
            public int Y;
            public bool Rotated;
            public ATOMask Mask;
            public ATOIsland Island;
        }

        internal sealed class AtlasBuilder
        {
            public int W;
            public int H;
            public ATOMask Occupancy;
            public readonly List<ATOIsland> Islands = new List<ATOIsland>();
            public readonly HashSet<Texture2D> Sources = new HashSet<Texture2D>();
            public readonly List<ATOUvGroup> Groups = new List<ATOUvGroup>();

            public AtlasBuilder(int w, int h)
            {
                W = w;
                H = h;
                Occupancy = ATOMask.Create(w / Granularity, h / Granularity);
            }

            public bool Find(ATOMask island, int pad, out Place place)
            {
                place = null;
                var variants = new[]
                {
                    new { rot = false, m = island },
                    new { rot = true, m = island.Rotated90() }
                };
                foreach (var v in variants)
                {
                    var iw = v.m.W + pad;
                    var ih = v.m.H + pad;
                    var maxX = Occupancy.W - iw;
                    var maxY = Occupancy.H - ih;
                    if (maxX < 0 || maxY < 0) continue;
                    for (var y = 0; y <= maxY; y++)
                    {
                        for (var x = 0; x <= maxX; x++)
                        {
                            if (!Fits(v.m, x, y, pad)) continue;
                            place = new Place { X = x, Y = y, Rotated = v.rot, Mask = v.m };
                            return true;
                        }
                    }
                }

                return false;
            }

            private bool Fits(ATOMask m, int x, int y, int pad)
            {
                for (var iy = 0; iy < m.H; iy++)
                {
                    for (var ix = 0; ix < m.W; ix++)
                    {
                        if (!m.Get(ix, iy)) continue;
                        if (Occupancy.Get(x + ix, y + iy)) return false;
                    }
                }

                return true;
            }

            public void Stamp(Place p)
            {
                for (var iy = 0; iy < p.Mask.H; iy++)
                for (var ix = 0; ix < p.Mask.W; ix++)
                {
                    if (p.Mask.Get(ix, iy)) Occupancy.Set(p.X + ix, p.Y + iy, true);
                }
            }
        }
    }

    internal static class ATOWholeTexture
    {
        public static void Scale(ATOState state)
        {
            var seen = new HashSet<Texture2D>();
            foreach (var isl in state.Islands)
            {
                if (isl.Source == null || !seen.Add(isl.Source)) continue;
                var sx = 1f;
                var sy = 1f;
                foreach (var o in state.Islands)
                {
                    if (o.Source != isl.Source) continue;
                    sx = Mathf.Min(sx, o.Scale.x);
                    sy = Mathf.Min(sy, o.Scale.y);
                }

                var w = Mathf.Max(1, Mathf.RoundToInt(isl.Source.width * sx));
                var h = Mathf.Max(1, Mathf.RoundToInt(isl.Source.height * sy));
                if (w == isl.Source.width && h == isl.Source.height)
                {
                    state.TextureReplace[isl.Source] = isl.Source;
                    continue;
                }

                var resized = ATOComposer.ResampleTexture(state, isl.Source, w, h, false);
                if (resized == null) continue;
                resized.name = AvatarTextureOptimizer.AtlasNamePrefix + isl.Source.name;
                state.TextureReplace[isl.Source] = resized;
                state.Generated.Add(resized);
                state.Report.ResultPixels += (long)w * h;
                state.Report.SourcePixels += (long)isl.Source.width * isl.Source.height;
                state.Log.Info("scaled texture " + isl.Source.name + " -> " + w + "x" + h);
            }
        }
    }
}
