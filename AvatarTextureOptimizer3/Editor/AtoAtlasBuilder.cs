// English: Type-group atlas build. UV-group is the pack atom. Shared island layout across layers.
// 中文：按类型组建图集。装箱原子是 UV 组。各图层（主色/法线/蒙版）共用同一套岛坐标。
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using net.fosa.ato;
using UnityEngine;
using Object = UnityEngine.Object;

namespace net.fosa.ato.editor
{
    public static class AtoAtlasBuilder
    {
        public struct LayerKey
        {
            public AtoTextureClass Class;
            public bool Linear;
            public FilterMode Filter;
            public override string ToString() => $"{Class}|{Linear}|{Filter}";
        }

        public static List<AtoAtlasResult> Build(
            BuildContext ctx, AtoPlatformSettings s, AtoPlatform platform,
            List<AtoTypeGroup> typeGroups, AtoTextureCache cache,
            Dictionary<Texture2D, Texture2D> texReplace, AtoBakeReport report, AtoCancel cancel)
        {
            var results = new List<AtoAtlasResult>();
            int maxEdge = AtoPlatformUtil.MaxAtlasEdge(platform);
            var pool = AtoPacker.CandidatePool(s.experimentalNpot, maxEdge);
            int minPad = (int)s.minPadding;
            int atlasIndex = 0;
            var remappedMeshes = new HashSet<int>();

            foreach (var tg in typeGroups)
            {
                cancel.ThrowIfCanceled();
                var groups = tg.UvGroups.Where(g => !g.Whitelisted && !g.SkipAtlasOnly && g.Islands.Count > 0).ToList();
                if (groups.Count == 0) continue;

                // Atom = texture + all UV groups that reference it (all islands of one tex stay in one atlas).
                var texToGroups = new Dictionary<Texture2D, List<AtoUvGroup>>();
                foreach (var g in groups)
                foreach (var b in g.Bindings)
                {
                    if (b.Texture == null || !b.Eligible) continue;
                    if (!texToGroups.TryGetValue(b.Texture, out var list))
                    { list = new List<AtoUvGroup>(); texToGroups[b.Texture] = list; }
                    if (!list.Contains(g)) list.Add(g);
                }

                var atoms = new List<(Texture2D tex, List<AtoUvGroup> ugs, List<AtoPackedIsland> islands, int area)>();
                foreach (var kv in texToGroups)
                {
                    var islands = UniqueIslands(kv.Value, kv.Key, maxEdge, minPad);
                    int area = islands.Sum(i => i.GW * i.GH);
                    atoms.Add((kv.Key, kv.Value, islands, area));
                }
                atoms.Sort((a, b) => b.area.CompareTo(a.area));

                var queues = new List<List<int>>();
                queues.Add(new List<int>());
                for (int ai = 0; ai < atoms.Count; ai++)
                {
                    bool placed = false;
                    foreach (var q in queues)
                    {
                        if (CanFit(q, ai, atoms, pool, maxEdge, minPad))
                        { q.Add(ai); placed = true; break; }
                    }
                    if (placed) continue;
                    var solo = new List<AtoPackedIsland>(atoms[ai].islands);
                    var dest = new List<AtoPackedIsland>();
                    if (!AtoPacker.TryPack(solo, maxEdge, maxEdge, AtoPacker.PaddingFor(maxEdge, minPad), dest))
                    {
                        report.Warnings.Add("atlas fail " + atoms[ai].tex.name);
                        AtoLog.Warn("Cannot atlas " + atoms[ai].tex.name);
                        try
                        {
                            ErrorReport.ReportError(AtoErrors.Localizer, ErrorSeverity.NonFatal,
                                "warn.atlas_fail", atoms[ai].tex.name);
                        }
                        catch { }
                        foreach (var g in atoms[ai].ugs) g.SkipAtlasOnly = true;
                        continue;
                    }
                    queues.Add(new List<int> { ai });
                }

                foreach (var q in queues)
                {
                    if (q.Count == 0) continue;
                    // Unique islands by UV-group identity (shared layout).
                    var layoutIslands = new List<AtoPackedIsland>();
                    var seenIsl = new HashSet<AtoIsland>();
                    foreach (var idx in q)
                    foreach (var isl in atoms[idx].islands)
                        if (seenIsl.Add(isl.Island)) layoutIslands.Add(isl);

                    int need = layoutIslands.Sum(i => i.GW * i.GH) * AtoPacker.Granule * AtoPacker.Granule;
                    AtoAtlasResult layout = null;
                    foreach (var c in pool.Where(sz => (long)sz.x * sz.y >= need))
                    {
                        var dest = new List<AtoPackedIsland>();
                        int pad = AtoPacker.PaddingFor(Mathf.Max(c.x, c.y), minPad);
                        if (!AtoPacker.TryPack(layoutIslands, c.x, c.y, pad, dest)) continue;
                        layout = new AtoAtlasResult { Width = c.x, Height = c.y, TypeKey = tg.Key };
                        layout.Items.AddRange(dest);
                        layout.Utilization = need / (float)(c.x * c.y);
                        break;
                    }
                    if (layout == null) continue;

                    // Secondary layer shrink if all non-primary islands want smaller size.
                    MaybeShrinkSecondary(layout, tg, cache, minPad);

                    var layers = new Dictionary<string, List<(Texture2D src, AtoTextureClass cls, bool lin, bool alpha)>>();
                    foreach (var idx in q)
                    {
                        var tex = atoms[idx].tex;
                        var dec = cache.Get(tex);
                        var cls = ClassOf(tex, atoms[idx].ugs);
                        var key = LayerBucket(cls);
                        if (!layers.TryGetValue(key, out var list))
                        { list = new List<(Texture2D, AtoTextureClass, bool, bool)>(); layers[key] = list; }
                        list.Add((tex, cls, dec != null && dec.Linear, dec != null && dec.HasAlpha));
                    }

                    var layerAtlas = new Dictionary<string, Texture2D>();
                    foreach (var lk in layers)
                    {
                        bool lin = lk.Value.Any(v => v.lin) || lk.Key == "normal";
                        bool alpha = lk.Value.Any(v => v.alpha);
                        // One atlas per layer: stamp each source into the SHARED island holes that belong to it.
                        // If multiple sources share the layer (different UV groups), they share one atlas.
                        var items = new List<AtoPackedIsland>();
                        foreach (var src in lk.Value)
                        foreach (var packed in layout.Items)
                        {
                            bool uses = false;
                            foreach (var idx in q)
                            {
                                if (atoms[idx].tex != src.src) continue;
                                if (atoms[idx].ugs.Any(g => g.Islands.Contains(packed.Island)))
                                { uses = true; break; }
                            }
                            if (!uses) continue;
                            items.Add(new AtoPackedIsland
                            {
                                Island = packed.Island, Source = src.src,
                                X = packed.X, Y = packed.Y, W = packed.W, H = packed.H,
                                Rotated = packed.Rotated
                            });
                        }
                        var name = $"ATO_{lk.Key}_{atlasIndex}_{layout.Width}x{layout.Height}";
                        var atlas = AtoApply.ComposeAtlas(ctx, items, layout.Width, layout.Height,
                            lin, alpha, name, cache, true, false);
                        var cls = lk.Value[0].cls;
                        bool mip = MipFor(s.compression, cls, alpha);
                        var choice = ChoiceFor(s.compression, cls);
                        AtoFormat.Apply(atlas, platform, choice, cls, alpha, lin, s.experimentalNpot, mip);
                        layerAtlas[lk.Key] = atlas;
                        foreach (var src in lk.Value) texReplace[src.src] = atlas;
                        report.Add($"Atlas {name} util={layout.Utilization:P1} layer={lk.Key} src=[{string.Join(",", lk.Value.Select(v => v.src.name))}] islands={items.Count}");
                    }
                    atlasIndex++;
                    results.Add(layout);

                    RemapOnce(ctx, layout, remappedMeshes);
                }
            }
            return results;
        }

        private static List<AtoPackedIsland> UniqueIslands(List<AtoUvGroup> ugs, Texture2D tex, int maxEdge, int minPad)
        {
            var list = new List<AtoPackedIsland>();
            var seen = new HashSet<AtoIsland>();
            int pad = AtoPacker.PaddingFor(maxEdge, minPad);
            foreach (var g in ugs)
            foreach (var isl in g.Islands)
            {
                if (!seen.Add(isl)) continue;
                list.Add(AtoPacker.Rasterize(isl, tex, tex.width, tex.height, pad));
            }
            return list;
        }

        private static bool CanFit(List<int> q, int ai,
            List<(Texture2D tex, List<AtoUvGroup> ugs, List<AtoPackedIsland> islands, int area)> atoms,
            List<Vector2Int> pool, int maxEdge, int minPad)
        {
            var trial = new List<AtoPackedIsland>();
            var seen = new HashSet<AtoIsland>();
            void add(List<AtoPackedIsland> src)
            {
                foreach (var p in src)
                    if (seen.Add(p.Island)) trial.Add(p);
            }
            foreach (var i in q) add(atoms[i].islands);
            add(atoms[ai].islands);
            int need = trial.Sum(a => a.GW * a.GH) * AtoPacker.Granule * AtoPacker.Granule;
            foreach (var c in pool)
            {
                if ((long)c.x * c.y < need) continue;
                var dest = new List<AtoPackedIsland>();
                if (AtoPacker.TryPack(trial, c.x, c.y, AtoPacker.PaddingFor(Mathf.Max(c.x, c.y), minPad), dest))
                    return true;
            }
            return false;
        }

        private static void MaybeShrinkSecondary(AtoAtlasResult layout, AtoTypeGroup tg, AtoTextureCache cache, int minPad)
        {
            if (!tg.HasNormal && !tg.HasMask) return;
            // If every non-albedo island already sits well below atlas size, we keep layout (UV group lock).
            // Actual pixel shrink of a whole secondary atlas is applied at compose time by smaller RT if
            // all secondary sources are solid/low-frequency. Conservative: skip if any island scale == 1.
            bool allSmall = layout.Items.All(i => i.Island.ScaleU < 0.51f && i.Island.ScaleV < 0.51f);
            if (!allSmall) return;
            AtoLog.VerboseInfo($"Type group {tg.Key}: secondary layers uniformly low-res (layout kept for UV lock).");
        }

        private static AtoTextureClass ClassOf(Texture2D tex, List<AtoUvGroup> ugs)
        {
            AtoTextureClass worst = AtoTextureClass.Unknown;
            foreach (var g in ugs)
            foreach (var b in g.Bindings)
            {
                if (b.Texture != tex) continue;
                if (b.Class == AtoTextureClass.Normal) return AtoTextureClass.Normal;
                if (b.Class == AtoTextureClass.TransparentAlbedo) worst = b.Class;
                else if (worst == AtoTextureClass.Unknown) worst = b.Class;
            }
            return worst;
        }

        private static string LayerBucket(AtoTextureClass c)
        {
            if (c == AtoTextureClass.Normal) return "normal";
            if (c == AtoTextureClass.Gray || c == AtoTextureClass.Mask) return "mask";
            return "albedo";
        }

        private static bool MipFor(AtoCompressionSet c, AtoTextureClass cls, bool alpha)
        {
            if (c == null) return true;
            if (cls == AtoTextureClass.Normal) return c.mipStreamingNormal;
            if (cls == AtoTextureClass.Gray || cls == AtoTextureClass.Mask) return c.mipStreamingGray;
            return alpha ? c.mipStreamingTransparent : c.mipStreamingOpaque;
        }

        private static AtoSafeCompression ChoiceFor(AtoCompressionSet c, AtoTextureClass cls)
        {
            if (c == null) return AtoSafeCompression.Balanced;
            if (cls == AtoTextureClass.Normal) return c.normal;
            if (cls == AtoTextureClass.Gray || cls == AtoTextureClass.Mask) return c.gray;
            if (cls == AtoTextureClass.TransparentAlbedo) return c.transparent;
            return c.opaque;
        }

        private static void RemapOnce(BuildContext ctx, AtoAtlasResult layout, HashSet<int> remapped)
        {
            var byMesh = new Dictionary<Mesh, List<AtoPackedIsland>>();
            foreach (var it in layout.Items)
            {
                if (it.Island?.Mesh == null) continue;
                if (!byMesh.TryGetValue(it.Island.Mesh, out var list))
                { list = new List<AtoPackedIsland>(); byMesh[it.Island.Mesh] = list; }
                list.Add(it);
            }
            foreach (var kv in byMesh)
            {
                var src = kv.Key;
                Mesh clone;
                if (remapped.Contains(src.GetInstanceID()) && src.name.EndsWith("_ATO"))
                    clone = src;
                else
                {
                    clone = Object.Instantiate(src);
                    clone.name = src.name + "_ATO";
                    remapped.Add(clone.GetInstanceID());
                    remapped.Add(src.GetInstanceID());
                }
                var channels = kv.Value.Select(i => i.Island.UvChannel).Distinct();
                foreach (var ch in channels)
                {
                    var uvs = new List<Vector2>();
                    clone.GetUVs(ch, uvs);
                    if (uvs.Count == 0) src.GetUVs(ch, uvs);
                    foreach (var it in kv.Value)
                        if (it.Island.UvChannel == ch)
                            AtoApply.RemapIslandUvs(uvs, it.Island, it, layout.Width, layout.Height);
                    clone.SetUVs(ch, uvs);
                }
                ctx.AssetSaver.SaveAsset(clone);
                foreach (var it in kv.Value)
                {
                    it.Island.Mesh = clone;
                    if (it.Island.Renderer) Assign(it.Island.Renderer, clone);
                }
            }
        }

        private static void Assign(Renderer r, Mesh mesh)
        {
            if (r is SkinnedMeshRenderer s) s.sharedMesh = mesh;
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf) mf.sharedMesh = mesh;
            }
        }
    }
}
