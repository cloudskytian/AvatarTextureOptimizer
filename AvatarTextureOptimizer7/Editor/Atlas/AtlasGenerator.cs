using System;
using System.Collections.Generic;
using Fosa.AvatarTextureOptimizer;
using Fosa.AvatarTextureOptimizer.API;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    public sealed class BuiltAtlas
    {
        public string Name;
        public Texture2D Texture;
        public AtoTextureKind Kind;
        public TypeGroup TypeGroup;
        public int Width;
        public int Height;
        public float Utilization;
        public readonly List<PackedIsland> Islands = new List<PackedIsland>();
        public readonly List<Texture2D> Sources = new List<Texture2D>();
        public bool HasAlpha;
    }

    public sealed class AtlasPlan
    {
        public readonly List<BuiltAtlas> Atlases = new List<BuiltAtlas>();
        /// <summary>UV group → list of packed islands (shared layout across kinds). / UV 组的共享布局。</summary>
        public readonly Dictionary<UvGroup, List<PackedIsland>> Layouts = new Dictionary<UvGroup, List<PackedIsland>>();
        public readonly HashSet<UvGroup> FailedAtlas = new HashSet<UvGroup>();
    }

    public static class AtlasGenerator
    {
        public static AtlasPlan Build(AtoSession session, AtoGraph graph)
        {
            var plan = new AtlasPlan();
            if (!session.GenerateAtlas)
            {
                session.Log.Info("Atlas generation disabled; whole-texture scale only.");
                return plan;
            }

            var candidates = CandidatePool.Build(session.MinAtlas, session.MaxAtlas, session.Npot);
            session.Log.Info("Candidate atlas pool: " + candidates.Count + "  npot=" + session.Npot +
                             " max=" + session.MaxAtlas);

            foreach (var tg in graph.TypeGroups)
            {
                PackTypeGroup(session, tg, candidates, plan);
            }

            session.Report.AtlasCount = plan.Atlases.Count;
            return plan;
        }

        static void PackTypeGroup(AtoSession session, TypeGroup tg, List<AtlasCandidate> pool, AtlasPlan plan)
        {
            // Sort UV groups by rasterized island area desc. / 按光栅化岛面积降序。
            var queue = new List<UvGroup>(tg.UvGroups);
            queue.RemoveAll(u => u.SkipAtlas || u.SkipAll || u.Islands.Count == 0);
            queue.Sort((a, b) => RasterArea(b).CompareTo(RasterArea(a)));

            var leftover = new List<UvGroup>();
            while (queue.Count > 0 || leftover.Count > 0)
            {
                if (queue.Count == 0)
                {
                    queue.AddRange(leftover);
                    leftover.Clear();
                    continue;
                }

                var packed = new List<UvGroup>();
                var failedThis = new List<UvGroup>();
                foreach (var ug in queue)
                {
                    var trial = new List<UvGroup>(packed) { ug };
                    if (CanPack(session, trial, pool[pool.Count - 1], tg.HasNormal))
                    {
                        packed.Add(ug);
                    }
                    else if (!CanPack(session, new List<UvGroup> { ug }, pool[pool.Count - 1], tg.HasNormal))
                    {
                        plan.FailedAtlas.Add(ug);
                        ug.SkipAtlas = true;
                        ug.SkipReason = AtoSkipReason.AtlasWouldNotFit;
                        session.WarnNdmf("warn.atlasFit", ug.Renderer.name + " UV" + ug.UvChannel);
                    }
                    else
                    {
                        failedThis.Add(ug);
                    }
                }

                if (packed.Count == 0)
                {
                    leftover.AddRange(failedThis);
                    queue.Clear();
                    if (leftover.Count == 0) break;
                    continue;
                }

                // Smallest candidate that can hold the packed set. / 能装下已装集合的最小候选。
                AtlasCandidate chosen = pool[pool.Count - 1];
                var totalCells = 0;
                foreach (var ug in packed) totalCells += RasterArea(ug);
                foreach (var c in pool)
                {
                    var cCells = (c.Width / BitmaskRaster.Granularity) * (c.Height / BitmaskRaster.Granularity);
                    if (cCells < totalCells) continue;
                    if (CanPack(session, packed, c, tg.HasNormal))
                    {
                        chosen = c;
                        break;
                    }
                }

                EmitAtlas(session, packed, chosen, tg, plan);
                queue = failedThis;
            }
        }

        static int RasterArea(UvGroup ug)
        {
            int a = 0;
            foreach (var isl in ug.Islands)
            {
                var w = (Mathf.Max(1, isl.ScaledW) + BitmaskRaster.Granularity - 1) / BitmaskRaster.Granularity;
                var h = (Mathf.Max(1, isl.ScaledH) + BitmaskRaster.Granularity - 1) / BitmaskRaster.Granularity;
                a += w * h;
            }

            return a;
        }

        static bool CanPack(AtoSession session, List<UvGroup> groups, AtlasCandidate cand, bool hasNormal)
        {
            var pad = CandidatePool.PaddingFor(Mathf.Max(cand.Width, cand.Height), session.MinPadding);
            using (var packer = new BlfPacker())
            {
                packer.Reset(cand.Width, cand.Height, pad);
                var allowRot = !hasNormal; // never rotate tangent-space maps
                foreach (var ug in groups)
                {
                    foreach (var isl in ug.Islands)
                    {
                        using (var mask = BitmaskRaster.RasterizeIsland(isl, isl.ScaledW, isl.ScaledH, Allocator.TempJob))
                        {
                            if (!packer.TryPlace(mask, allowRot, out _, out _, out _, out var used))
                                return false;
                            if (used.Bits.IsCreated && used.Bits != mask.Bits) used.Dispose();
                        }
                    }
                }
            }

            return true;
        }

        static void EmitAtlas(AtoSession session, List<UvGroup> groups, AtlasCandidate cand, TypeGroup tg, AtlasPlan plan)
        {
            var pad = CandidatePool.PaddingFor(Mathf.Max(cand.Width, cand.Height), session.MinPadding);
            var allowRot = !tg.HasNormal;
            var layout = new List<PackedIsland>();
            using (var packer = new BlfPacker())
            {
                packer.Reset(cand.Width, cand.Height, pad);
                foreach (var ug in groups)
                {
                    var ugLayout = new List<PackedIsland>();
                    foreach (var isl in ug.Islands)
                    {
                        var mask = BitmaskRaster.GetOrRasterize(isl, isl.ScaledW, isl.ScaledH, Allocator.Persistent);
                        if (!packer.TryPlace(mask, allowRot, out var x, out var y, out var rot, out var used))
                        {
                            session.Log.Warn("Unexpected pack failure while emitting atlas for " + ug.Renderer.name);
                            plan.FailedAtlas.Add(ug);
                            ug.SkipAtlas = true;
                            if (used.Bits.IsCreated && used.Bits != mask.Bits) used.Dispose();
                            return;
                        }

                        if (used.Bits.IsCreated && used.Bits != mask.Bits) used.Dispose();
                        var p = new PackedIsland
                        {
                            Island = isl,
                            X = x + pad,
                            Y = y + pad,
                            W = rot ? isl.ScaledH : isl.ScaledW,
                            H = rot ? isl.ScaledW : isl.ScaledH,
                            Rotated = rot,
                            Source = isl.SourceTexture
                        };
                        ugLayout.Add(p);
                        layout.Add(p);
                    }

                    plan.Layouts[ug] = ugLayout;
                }

                var util = packer.Utilization();
                // One atlas per kind in the type group, sharing layout. / 类型组内每种贴图一张图集，共享布局。
                foreach (var kind in DistinctKinds(groups))
                {
                    var atlas = BlitAtlas(session, layout, groups, cand, kind, tg, util);
                    if (atlas != null)
                    {
                        plan.Atlases.Add(atlas);
                        session.Report.AtlasLines.Add(string.Format(
                            "{0} {1}x{2} util={3:0.0%} sources={4} islands={5} kind={6}",
                            atlas.Name, atlas.Width, atlas.Height, atlas.Utilization,
                            atlas.Sources.Count, atlas.Islands.Count, kind));
                        session.Log.Info("[ATO] Atlas " + atlas.Name + " from [" +
                                         string.Join(", ", atlas.Sources.ConvertAll(s => s != null ? s.name : "?")) +
                                         "] islands=" + atlas.Islands.Count +
                                         " size=" + atlas.Width + "x" + atlas.Height +
                                         " util=" + atlas.Utilization.ToString("P1"));
                        foreach (var hook in AtoExtensions.GetAtlasHooks())
                        {
                            try { hook?.OnAtlasBuilt(atlas.Name, atlas.Texture, atlas.Islands.Count, atlas.Utilization); }
                            catch (Exception e) { session.Log.Warn("Atlas hook " + hook.Id + ": " + e.Message); }
                        }
                    }
                }
            }
        }

        static List<AtoTextureKind> DistinctKinds(List<UvGroup> groups)
        {
            var set = new HashSet<AtoTextureKind>();
            foreach (var ug in groups)
            foreach (var b in ug.Bindings)
                if (b.Slot != null)
                    set.Add(b.Slot.Kind);
            if (set.Count == 0) set.Add(AtoTextureKind.Albedo);
            return new List<AtoTextureKind>(set);
        }

        static BuiltAtlas BlitAtlas(AtoSession session, List<PackedIsland> layout, List<UvGroup> groups,
            AtlasCandidate cand, AtoTextureKind kind, TypeGroup tg, float util)
        {
            var name = "ATO_" + kind + "_" + cand.Width + "x" + cand.Height + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var linear = kind != AtoTextureKind.Albedo;
            var tex = new Texture2D(cand.Width, cand.Height, TextureFormat.RGBA32, false, linear)
            {
                name = name,
                filterMode = tg.Filter,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 1
            };
            var pixels = new Color[cand.Width * cand.Height];
            var hasAlpha = false;
            var sources = new HashSet<Texture2D>();

            foreach (var ug in groups)
            {
                Texture2D src = null;
                foreach (var b in ug.Bindings)
                {
                    if (b.Slot != null && b.Slot.Kind == kind)
                    {
                        src = b.Slot.Texture;
                        break;
                    }
                }

                if (src == null) continue;
                sources.Add(src);
                var dec = session.DecodeCache.Get(src, kind == AtoTextureKind.Normal);
                foreach (var p in layout)
                {
                    if (p.Island == null || p.Island.Mesh != ug.Mesh || p.Island.UvChannel != ug.UvChannel) continue;
                    BlitIsland(pixels, cand.Width, cand.Height, p, dec, src, kind, ref hasAlpha);
                }
            }

            PullPushBleed.Fill(pixels, cand.Width, cand.Height, hasAlpha);
            tex.SetPixels(pixels);
            tex.Apply(false, false);
            tex.wrapMode = TextureWrapMode.Clamp;

            var atlas = new BuiltAtlas
            {
                Name = name,
                Texture = tex,
                Kind = kind,
                TypeGroup = tg,
                Width = cand.Width,
                Height = cand.Height,
                Utilization = util,
                HasAlpha = hasAlpha
            };
            atlas.Islands.AddRange(layout);
            atlas.Sources.AddRange(sources);
            session.Track(tex);
            session.Save(tex);
            session.Report.OutputPixels += (long)cand.Width * cand.Height;
            return atlas;

            bool planTryGet(UvGroup ug) => true;
            bool sessionDummy(out List<PackedIsland> l) { l = layout; return true; }
        }

        static void BlitIsland(Color[] dest, int dw, int dh, PackedIsland p, TextureDecodeCache.Decoded dec,
            Texture2D src, AtoTextureKind kind, ref bool hasAlpha)
        {
            var isl = p.Island;
            var crop = QualityEvaluator.CropIsland(dec, isl, dec.Width, dec.Height);
            var scaled = QualityEvaluator.Resample(crop, isl.OrigPixelW, isl.OrigPixelH, isl.ScaledW, isl.ScaledH,
                kind == AtoTextureKind.Albedo);
            for (int y = 0; y < p.H; y++)
            for (int x = 0; x < p.W; x++)
            {
                int sx, sy;
                if (p.Rotated)
                {
                    // 90 CW: dest(x,y) = src(y, Hsrc-1-x) where Hsrc = ScaledH
                    sx = y;
                    sy = isl.ScaledH - 1 - x;
                }
                else
                {
                    sx = x;
                    sy = y;
                }

                sx = Mathf.Clamp(sx, 0, isl.ScaledW - 1);
                sy = Mathf.Clamp(sy, 0, isl.ScaledH - 1);
                var c = scaled[sy * isl.ScaledW + sx];
                if (kind == AtoTextureKind.Normal)
                {
                    var n = new Vector3(c.r, c.g, c.b).normalized;
                    c = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                }

                if (c.a < 0.999f) hasAlpha = true;
                var dx = p.X + x;
                var dy = p.Y + y;
                if ((uint)dx < (uint)dw && (uint)dy < (uint)dh)
                    dest[dy * dw + dx] = c;
            }
        }
    }
}
