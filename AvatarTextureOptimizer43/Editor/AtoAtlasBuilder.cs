using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// Type-group aware atlasing: shared UV layout, parallel sheets for overlapping sources,
    /// secondary-atlas downscale, whitelist siblings skip UV rewrite.
    /// 类型组装箱：共享 UV 布局、重叠源平行图集、副图集降分辨率、白名单同 UV 不改 UV。
    /// </summary>
    public static class AtoAtlasBuilder
    {
        public static void Run(
            BuildContext ctx,
            List<AtoTextureRef> eligible,
            List<AtoTextureRef> allRefs,
            AtoAnimInfo anim,
            AtoResolvedSettings settings,
            Dictionary<Texture2D, Texture2D> texRemap,
            Dictionary<Mesh, Mesh> meshRemap,
            AtoReport report,
            AtoProgress progress,
            AtoCache cache,
            AtoBakeContext bake = null)
        {
            // UV connected components (mesh,uv) ↔ texture, including whitelist siblings.
            // 含白名单兄弟的 UV 连通分量。
            var uf = new List<int>();
            int Find(int a) { while (uf[a] != a) { uf[a] = uf[uf[a]]; a = uf[a]; } return a; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) uf[b] = a; }
            var texId = new Dictionary<Texture2D, int>();
            var slotId = new Dictionary<(Mesh, int), int>();
            int Tid(Texture2D t)
            {
                if (!texId.TryGetValue(t, out var id)) { id = uf.Count; texId[t] = id; uf.Add(id); }
                return id;
            }
            int Sid(Mesh m, int c)
            {
                var k = (m, c);
                if (!slotId.TryGetValue(k, out var id)) { id = uf.Count; slotId[k] = id; uf.Add(id); }
                return id;
            }

            foreach (var tr in allRefs)
            {
                if (tr.Texture == null || tr.Mesh == null) continue;
                Union(Tid(tr.Texture), Sid(tr.Mesh, tr.UvChannel));
            }

            var groups = new Dictionary<int, List<AtoTextureRef>>();
            foreach (var tr in allRefs)
            {
                if (tr.Texture == null || tr.Mesh == null) continue;
                int g = Find(Tid(tr.Texture));
                if (!groups.TryGetValue(g, out var list)) { list = new List<AtoTextureRef>(); groups[g] = list; }
                list.Add(tr);
            }
            report.UvGroups = groups.Count;
            AtoLog.Info("UV groups=" + groups.Count + " (incl. whitelist siblings)");

            var pool = AtoAtlas.BuildPool(settings);
            int atlasIndex = 0;
            int gi = 0;
            foreach (var kv in groups)
            {
                gi++;
                progress.Set(AtoLoc.T("ato.progress.pack") + " " + gi + "/" + groups.Count,
                    0.35f + 0.4f * gi / Math.Max(1, groups.Count));
                ProcessGroup(ctx, kv.Value, anim, settings, texRemap, meshRemap, report, pool,
                    cache, ref atlasIndex);
            }

            foreach (var r in ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null && meshRemap.TryGetValue(smr.sharedMesh, out var nm))
                    smr.sharedMesh = nm;
                if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf && mf.sharedMesh && meshRemap.TryGetValue(mf.sharedMesh, out var nm2))
                        mf.sharedMesh = nm2;
                }
            }
        }

        static void ProcessGroup(
            BuildContext ctx, List<AtoTextureRef> gRefs, AtoAnimInfo anim,
            AtoResolvedSettings settings,
            Dictionary<Texture2D, Texture2D> texRemap, Dictionary<Mesh, Mesh> meshRemap,
            AtoReport report, List<AtoAtlas.Candidate> pool, AtoCache cache, ref int atlasIndex)
        {
            bool anyWhite = gRefs.Any(x => x.Whitelisted || !x.Eligible);
            var canAtlas = gRefs.Where(x => x.Eligible && !x.Whitelisted && x.Texture != null).ToList();

            if (anyWhite)
            {
                // Whitelist sibling: skip atlasing (do not rewrite UVs), still whole-texture scale + import.
                // 白名单同 UV：跳过图集化不改 UV，仍做整图缩放与导入参数优化。
                report.Warnings.Add("UV group has whitelist/ineligible member — skip atlas, scale remaining");
                AtoLog.Info("UV group whitelist sibling → no atlas, scale " + canAtlas.Count + " textures");
                if (canAtlas.Count > 0)
                    AtoPipeline.ScaleWholeTexturesPublic(ctx, canAtlas, settings, texRemap, report, cache);
                return;
            }
            if (canAtlas.Count == 0) return;

            var filters = canAtlas.Select(x => x.Filter).Distinct().ToList();
            var linearFlags = canAtlas.Select(x => x.Linear).Distinct().ToList();
            if (filters.Count > 2)
            {
                report.Warnings.Add("UV group mixed filterMode — skip atlas, scale only");
                AtoPipeline.ScaleWholeTexturesPublic(ctx, canAtlas, settings, texRemap, report, cache);
                return;
            }

            var islandsByMesh = new Dictionary<(Mesh, int), List<AtoIsland>>();
            bool groupFail = false;
            foreach (var tr in canAtlas.GroupBy(x => (x.Mesh, x.UvChannel)))
            {
                var mesh = tr.Key.Mesh;
                int ch = tr.Key.UvChannel;
                int tw = tr.Max(x => x.Texture != null ? x.Texture.width : 1);
                int th = tr.Max(x => x.Texture != null ? x.Texture.height : 1);
                var islands = AtoUvIslands.Extract(mesh, ch, tw, th, out var fail);
                if (fail != null)
                {
                    report.Warnings.Add(mesh.name + " UV" + ch + " " + fail);
                    groupFail = true; break;
                }
                if (islands.Any(i => i.OverflowUnrecoverable || i.Wrapped))
                {
                    report.Warnings.Add(mesh.name + " UV" + ch + " wrap/overflow → skip atlas");
                    groupFail = true; break;
                }
                foreach (var isl in islands)
                {
                    float area = isl.WorldArea;
                    foreach (var user in canAtlas.Where(x => x.Mesh == mesh))
                    {
                        if (user.Renderer is SkinnedMeshRenderer smr)
                            area = Mathf.Max(area, AtoUvIslands.MaxBlendshapeWorldArea(smr, isl));
                        float sm = AtoAnimationScanner.MaxHierarchyScale(user.Renderer.transform,
                            ctx.AvatarRootTransform, anim);
                        float ls = user.Renderer.transform.lossyScale.sqrMagnitude;
                        area *= Mathf.Max(1f, sm * sm / Math.Max(1e-8f, ls));
                    }
                    isl.WorldArea = area;
                }
                islandsByMesh[tr.Key] = islands;
                report.Islands += islands.Count;
            }
            if (groupFail)
            {
                AtoPipeline.ScaleWholeTexturesPublic(ctx, canAtlas, settings, texRemap, report, cache);
                return;
            }

            bool lossless = settings.quality.IsLossless || settings.qualityPreset == AtoQualityPreset.Lossless;
            var allIslands = islandsByMesh.SelectMany(x => x.Value).ToList();

            // Per-island scale: barrel across every texture covering that island.
            // 岛缩放：覆盖该岛的所有贴图取木桶最大。
            var scaleByClass = new Dictionary<AtoTextureClass, List<float>>();
            foreach (var isl in allIslands)
            {
                var users = canAtlas.Where(x => x.Mesh == isl.Mesh && x.UvChannel == isl.UvChannel
                                                && x.MaterialSlot == isl.Submesh).ToList();
                if (users.Count == 0)
                    users = canAtlas.Where(x => x.Mesh == isl.Mesh && x.UvChannel == isl.UvChannel).ToList();
                float su = 0.01f, sv = 0.01f;
                foreach (var u in users)
                {
                    var px = cache.Get(u.Texture);
                    int x0 = Mathf.Clamp(isl.PixelBounds.x, 0, u.Texture.width - 1);
                    int y0 = Mathf.Clamp(isl.PixelBounds.y, 0, u.Texture.height - 1);
                    int iw = Mathf.Clamp(isl.PixelBounds.width, 1, u.Texture.width - x0);
                    int ih = Mathf.Clamp(isl.PixelBounds.height, 1, u.Texture.height - y0);
                    var crop = Crop(px, u.Texture.width, u.Texture.height, x0, y0, iw, ih);
                    bool solid = AtoTextureUtil.IsSolidColor(crop);
                    isl.SolidColor |= solid;
                    float dens = AtoUvIslands.DensityPxPerMeter(isl, isl.WorldArea, u.Texture.width, u.Texture.height);
                    float minS = 1f;
                    if (dens > 1e-6f)
                    {
                        float target = Mathf.Clamp(dens, settings.minPixelDensity, settings.maxPixelDensity);
                        minS = Mathf.Clamp(target / dens, 1f / Math.Max(iw, ih), 1f);
                    }
                    var sc = AtoQuality.SearchScale(crop, iw, ih, u.Texture.isDataSRGB, u.Class, u.AlphaMode, u.Cutoff,
                        settings.quality, minS, lossless, solid);
                    su = Mathf.Max(su, sc.x);
                    sv = Mathf.Max(sv, sc.y);
                    if (!scaleByClass.TryGetValue(u.Class, out var lst))
                    {
                        lst = new List<float>();
                        scaleByClass[u.Class] = lst;
                    }
                    lst.Add(Mathf.Max(sc.x, sc.y));
                }
                isl.ScaleU = Mathf.Clamp01(su);
                isl.ScaleV = Mathf.Clamp01(sv);
            }

            int needed = 0;
            foreach (var isl in allIslands)
            {
                int iw = Math.Max(1, Mathf.CeilToInt(isl.PixelBounds.width * isl.ScaleU));
                int ih = Math.Max(1, Mathf.CeilToInt(isl.PixelBounds.height * isl.ScaleV));
                needed += iw * ih;
            }
            int padGuess = AtoAtlas.PaddingFor(settings.maxAtlasSide, settings.minPadding);
            needed += allIslands.Count * padGuess * padGuess;

            bool hasNormal = canAtlas.Any(x => x.Class == AtoTextureClass.Normal);
            bool allowRot = !hasNormal;
            var cands = AtoAtlas.FilterSort(pool, needed);
            AtoAtlas.Candidate? chosen = null;
            foreach (var c in cands)
            {
                int pad = AtoAtlas.PaddingFor(Math.Max(c.W, c.H), settings.minPadding);
                if (AtoAtlas.TryPack(allIslands, c, pad, allowRot))
                {
                    chosen = c; break;
                }
            }
            if (chosen == null)
            {
                report.Warnings.Add("UV group does not fit max atlas — skip atlas");
                AtoLog.Warn("Single UV group cannot fit " + settings.maxAtlasSide + " — fallback scale");
                AtoPipeline.ScaleWholeTexturesPublic(ctx, canAtlas, settings, texRemap, report, cache);
                return;
            }

            var cand = chosen.Value;
            int padding = AtoAtlas.PaddingFor(Math.Max(cand.W, cand.H), settings.minPadding);
            AtoLog.Info("Packed islands=" + allIslands.Count + " atlas=" + cand.W + "x" + cand.H
                        + " pad=" + padding + " rot=" + allowRot + " neededPx=" + needed);

            foreach (var pair in islandsByMesh)
            {
                var srcMesh = pair.Key.Mesh;
                int ch = pair.Key.UvChannel;
                if (!meshRemap.TryGetValue(srcMesh, out var cloned))
                {
                    cloned = AtoApply.CloneMesh(ctx, srcMesh);
                    meshRemap[srcMesh] = cloned;
                }
                foreach (var isl in pair.Value) isl.Mesh = cloned;
                foreach (var tr in canAtlas.Where(x => x.Mesh == srcMesh && x.UvChannel == ch))
                {
                    if (tr.Renderer is SkinnedMeshRenderer smr)
                        AtoAaoBridge.EvacuateIfNeeded(smr, cloned, ch, report);
                }
                AtoApply.RewriteUv(cloned, ch, pair.Value, cand.W, cand.H);
            }

            float albedoScale = Avg(scaleByClass, AtoTextureClass.Opaque, AtoTextureClass.Transparent);

            // Sort textures by rasterized island area desc — packing queue order.
            // 按光栅化面积降序形成贴图队列。
            var texOrder = canAtlas
                .Select(t => t.Texture)
                .Distinct()
                .OrderByDescending(t => AreaOf(t, canAtlas, allIslands, meshRemap))
                .ToList();

            var classes = canAtlas.Select(x => x.Class).Distinct().ToList();
            foreach (var cls in classes)
            {
                var classRefs = canAtlas.Where(x => x.Class == cls).ToList();
                var classTex = texOrder.Where(t => classRefs.Any(r => r.Texture == t)).ToList();

                // Greedy sheets: overlapping sources (animation swap) get parallel atlases;
                // disjoint submesh sources share one sheet.
                // 重叠源（动画切换）平行图集；不相交子网格共享一张。
                var sheets = new List<List<Texture2D>>();
                foreach (var t in classTex)
                {
                    var covered = CoveredKeys(t, classRefs, allIslands, meshRemap);
                    int found = -1;
                    for (int s = 0; s < sheets.Count; s++)
                    {
                        bool overlap = false;
                        foreach (var ot in sheets[s])
                        {
                            var oc = CoveredKeys(ot, classRefs, allIslands, meshRemap);
                            if (oc.Overlaps(covered)) { overlap = true; break; }
                        }
                        if (!overlap) { found = s; break; }
                    }
                    if (found < 0)
                    {
                        sheets.Add(new List<Texture2D> { t });
                    }
                    else sheets[found].Add(t);
                }

                bool linear = cls == AtoTextureClass.Normal || cls == AtoTextureClass.Gray;
                bool keepA0 = cls == AtoTextureClass.Transparent;
                var mips = settings.formats.ForClass(cls).mipAndStreaming;

                foreach (var sheet in sheets)
                {
                    atlasIndex++;
                    string name = AvatarTextureOptimizer.AtlasNamePrefix + cls + "_" + atlasIndex;
                    var sheetRefs = classRefs.Where(r => sheet.Contains(r.Texture)).ToList();

                    var atlasTex = AtoAtlas.Compose(name, cand.W, cand.H, allIslands, isl =>
                    {
                        Mesh origMesh = isl.Mesh;
                        foreach (var mk in meshRemap)
                            if (mk.Value == isl.Mesh) { origMesh = mk.Key; break; }
                        var u = sheetRefs.FirstOrDefault(x =>
                            (x.Mesh == origMesh || x.Mesh == isl.Mesh) && x.MaterialSlot == isl.Submesh);
                        if (u == null)
                            u = sheetRefs.FirstOrDefault(x => x.Mesh == origMesh || x.Mesh == isl.Mesh);
                        if (u == null) return Array.Empty<Color>();
                        var px = cache.Get(u.Texture);
                        int x0 = Mathf.Clamp(isl.PixelBounds.x, 0, u.Texture.width - 1);
                        int y0 = Mathf.Clamp(isl.PixelBounds.y, 0, u.Texture.height - 1);
                        int iw = Mathf.Clamp(isl.PixelBounds.width, 1, u.Texture.width - x0);
                        int ih = Mathf.Clamp(isl.PixelBounds.height, 1, u.Texture.height - y0);
                        var crop = Crop(px, u.Texture.width, u.Texture.height, x0, y0, iw, ih);
                        int dw = Math.Max(1, Mathf.RoundToInt(iw * isl.ScaleU));
                        int dh = Math.Max(1, Mathf.RoundToInt(ih * isl.ScaleV));
                        if (dw == iw && dh == ih) return crop;
                        if (cls == AtoTextureClass.Normal)
                            return AtoTextureUtil.ResampleNormal(crop, iw, ih, dw, dh);
                        bool premul = cls == AtoTextureClass.Transparent;
                        return AtoGpu.ResampleOrCpu(crop, iw, ih, dw, dh, premul,
                            u.Texture.isDataSRGB && cls != AtoTextureClass.Normal);
                    }, linear, mips, keepA0);

                    // Secondary type overall scale < albedo → shrink whole atlas.
                    // 该类型整体质量需求低于主色时缩小整张图集。
                    float typeScale = Avg(scaleByClass, cls);
                    if (cls != AtoTextureClass.Opaque && cls != AtoTextureClass.Transparent
                        && albedoScale > 1e-4f && typeScale < albedoScale * 0.75f)
                    {
                        float f = Mathf.Clamp(typeScale / albedoScale, 0.25f, 1f);
                        int nw = Math.Max(padding * 2, ((int)(cand.W * f) + 3) & ~3);
                        int nh = Math.Max(padding * 2, ((int)(cand.H * f) + 3) & ~3);
                        if (!settings.experimentalNpot)
                        {
                            nw = Mathf.ClosestPowerOfTwo(nw);
                            nh = Mathf.ClosestPowerOfTwo(nh);
                        }
                        nw = Mathf.Clamp(nw, 64, cand.W);
                        nh = Mathf.Clamp(nh, 64, cand.H);
                        if (nw < cand.W || nh < cand.H)
                        {
                            AtoLog.Info("Secondary atlas " + name + " downscale " + cand.W + "x" + cand.H
                                        + " -> " + nw + "x" + nh + " (typeScale=" + typeScale.ToString("0.00")
                                        + " albedo=" + albedoScale.ToString("0.00") + ")");
                            var shrunk = AtoAtlas.DownscaleWhole(atlasTex, nw, nh, linear, mips);
                            UnityEngine.Object.DestroyImmediate(atlasTex);
                            atlasTex = shrunk;
                        }
                    }

                    atlasTex.filterMode = AtoFormats.BestFilter(sheetRefs.Select(x => x.Filter));
                    atlasTex.anisoLevel = sheetRefs.Max(x => x.Texture != null ? x.Texture.anisoLevel : 1);
                    atlasTex.wrapMode = TextureWrapMode.Clamp;
                    bool linearOut = cls == AtoTextureClass.Normal || cls == AtoTextureClass.Gray;
                    atlasTex = AtoExport.Commit(ctx, atlasTex, cls, settings, report,
                        atlasTex.filterMode, atlasTex.anisoLevel, linearOut);
                    ObjectRegistry.RegisterReplacedObject(sheet[0], atlasTex);
                    if (bake != null) AtoApi.RaiseAtlasCreated(bake, atlasTex);

                    foreach (var t in sheet)
                    {
                        report.BytesBefore += AtoTextureUtil.UncompressedBytes(t);
                        texRemap[t] = atlasTex;
                    }
                    report.BytesAfter += AtoTextureUtil.UncompressedBytes(atlasTex);
                    float util = AtoAtlas.Utilization(allIslands, cand.W, cand.H);
                    var srcNames = string.Join(",", sheet.Select(t => t.name));
                    report.AddAtlas(name, atlasTex.width, atlasTex.height, allIslands.Count, util, srcNames);
                    report.Atlases++;
                    AtoLog.Info("Atlas " + name + " " + atlasTex.width + "x" + atlasTex.height
                                + " util=" + util.ToString("P1") + " src=[" + srcNames + "] islands="
                                + allIslands.Count);
                }
            }
            report.TypeGroups++;
        }

        static HashSet<(Mesh, int, int)> CoveredKeys(
            Texture2D tex, List<AtoTextureRef> classRefs, List<AtoIsland> islands,
            Dictionary<Mesh, Mesh> meshRemap)
        {
            var set = new HashSet<(Mesh, int, int)>();
            foreach (var r in classRefs)
            {
                if (r.Texture != tex) continue;
                set.Add((r.Mesh, r.UvChannel, r.MaterialSlot));
            }
            return set;
        }

        static int AreaOf(Texture2D t, List<AtoTextureRef> refs, List<AtoIsland> islands,
            Dictionary<Mesh, Mesh> meshRemap)
        {
            int a = 0;
            var slots = new HashSet<(Mesh, int)>();
            foreach (var r in refs)
                if (r.Texture == t) slots.Add((r.Mesh, r.UvChannel));
            foreach (var isl in islands)
            {
                Mesh orig = isl.Mesh;
                foreach (var kv in meshRemap)
                    if (kv.Value == isl.Mesh) { orig = kv.Key; break; }
                if (slots.Contains((orig, isl.UvChannel)) || slots.Contains((isl.Mesh, isl.UvChannel)))
                    a += Math.Max(1, isl.PixelBounds.width * isl.PixelBounds.height);
            }
            return a;
        }

        static float Avg(Dictionary<AtoTextureClass, List<float>> d, params AtoTextureClass[] cs)
        {
            double s = 0; int n = 0;
            foreach (var c in cs)
            {
                if (!d.TryGetValue(c, out var l)) continue;
                foreach (var v in l) { s += v; n++; }
            }
            return n == 0 ? 1f : (float)(s / n);
        }

        static Color[] Crop(Color[] src, int w, int h, int x, int y, int cw, int ch)
        {
            var d = new Color[cw * ch];
            for (int yy = 0; yy < ch; yy++)
            for (int xx = 0; xx < cw; xx++)
            {
                int sx = Mathf.Clamp(x + xx, 0, w - 1);
                int sy = Mathf.Clamp(y + yy, 0, h - 1);
                d[yy * cw + xx] = src[sy * w + sx];
            }
            return d;
        }
    }
}
