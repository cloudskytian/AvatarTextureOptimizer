using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Full bake pipeline. / 完整烘焙管线。
    /// </summary>
    public static class OptimizePipeline
    {
        public static void Run(BuildContext ctx, AvatarTextureOptimizer[] components)
        {
            var root = ctx.AvatarRootObject;
            var langMode = components[0].language;

            if (components.Length > 1)
            {
                ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.Error, "ato.error.duplicate");
                AtoLog.Error(AtoLoc.T("ato.error.duplicate", langMode));
                throw new InvalidOperationException("duplicate ATO");
            }

            var settings = components[0];
            AtoLog.Verbose = settings.verboseLog;
            if (!settings.HasAvatarDescriptor)
            {
                ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.Error, "ato.error.noDescriptor");
                AtoLog.Error(AtoLoc.T("ato.error.noDescriptor", langMode), settings);
                throw new InvalidOperationException("no VRCAvatarDescriptor");
            }

            foreach (var hook in AtoHookRegistry.GetHooks())
            {
                try { hook.OnBeforeOptimize(root, settings); }
                catch (Exception e) { AtoLog.Warn($"Hook {hook.Name} OnBefore: {e.Message}"); }
            }

            var report = new AtoReportData();
            var platform = settings.enablePlatformOverride ? settings.defaultPlatform : AvatarTextureOptimizer.DetectEditorPlatform();
            var platSet = settings.ActivePlatformSettings(platform);
            var q = settings.ActiveQuality;
            var folder = EnsureFolder(root.name);

            try
            {
                using (AtoLog.Time("ATO total"))
                {
                    AtoProgress.Update(langMode, "ato.progress.validate", 0.02f);

                    var whitelistTex = WhitelistCollector.Collect(root, settings.whitelist, out var whitelistObjs);
                    AtoLog.Info($"Whitelist textures: {whitelistTex.Count}");

                    AtoProgress.Update(langMode, "ato.progress.anim", 0.08f);
                    AnimationFacts facts;
                    using (AtoLog.Time("animation scan"))
                        facts = AnimationScanner.Scan(ctx, root);

                    AtoProgress.Update(langMode, "ato.progress.shader", 0.16f);
                    var renderers = CollectRenderers(root, facts);
                    var bindings = CollectBindings(renderers, facts, whitelistTex, settings, report);

                    AtoProgress.Update(langMode, "ato.progress.dedup", 0.24f);
                    TextureContentDedup.Apply(bindings, whitelistTex, ctx);

                    AtoProgress.Update(langMode, "ato.progress.uv", 0.36f);
                    var uvGroups = BuildUvGroups(bindings, settings, report);

                    AtoProgress.Update(langMode, "ato.progress.quality", 0.5f);
                    ScaleIslands(uvGroups, settings, q, report);

                    Dictionary<int, AtlasResult> atlases = new Dictionary<int, AtlasResult>();
                    if (settings.generateAtlas)
                    {
                        AtoProgress.Update(langMode, "ato.progress.atlas", 0.68f);
                        atlases = PackAtlases(uvGroups, settings, platform, platSet, folder, ctx, report);
                    }
                    else
                    {
                        AtoProgress.Update(langMode, "ato.progress.atlas", 0.68f);
                        ScaleWholeTextures(bindings, settings, q, platSet, folder, ctx, report);
                    }

                    AtoProgress.Update(langMode, "ato.progress.apply", 0.82f);
                    // Evacuate original UVs for AAO BEFORE we rewrite them.
                    // 在改写 UV 之前为 AAO 疏散原始 UV。
                    AaoBridge.EvacuateIfNeeded(root);
                    ApplyPass.Apply(ctx, root, settings, bindings, uvGroups, atlases, facts, folder, platSet, platform, report);

                    AtoProgress.Update(langMode, "ato.progress.importer", 0.92f);

                    AtoProgress.Update(langMode, "ato.progress.report", 0.97f);
                    EmitReport(settings, report);
                }
            }
            finally
            {
                // Strip component from baked avatar. / 从成品上移除自身。
                foreach (var c in root.GetComponentsInChildren<AvatarTextureOptimizer>(true))
                    Object.DestroyImmediate(c);
                AtoProgress.Clear();
            }

            foreach (var hook in AtoHookRegistry.GetHooks())
            {
                try { hook.OnAfterOptimize(root, settings); }
                catch (Exception e) { AtoLog.Warn($"Hook {hook.Name} OnAfter: {e.Message}"); }
            }
        }

        private static string EnsureFolder(string avatarName)
        {
            var safe = string.Join("_", avatarName.Split(Path.GetInvalidFileNameChars()));
            var root = AvatarTextureOptimizer.GeneratedAssetFolder;
            if (!AssetDatabase.IsValidFolder("Assets/_ATO_Generated"))
            {
                if (!AssetDatabase.IsValidFolder("Assets")) { /* always exists */ }
                AssetDatabase.CreateFolder("Assets", "_ATO_Generated");
            }
            var sub = root + "/" + safe + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            if (!AssetDatabase.IsValidFolder(sub))
            {
                AssetDatabase.CreateFolder(root, Path.GetFileName(sub));
            }
            return sub;
        }

        private static List<RendererRef> CollectRenderers(GameObject root, AnimationFacts facts)
        {
            var list = new List<RendererRef>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                Mesh mesh = null;
                bool skinned = false;
                if (r is SkinnedMeshRenderer smr)
                {
                    mesh = smr.sharedMesh;
                    skinned = true;
                }
                else if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    mesh = mf != null ? mf.sharedMesh : null;
                }
                if (mesh == null) continue;

                var path = PathUtil.RelativePath(root.transform, r.transform);
                bool enabled = r.enabled && r.gameObject.activeInHierarchy;
                bool animOn = facts.EnabledPaths.Contains(path);
                if (!enabled && !animOn) continue;

                float scaleMul = 1f;
                var t = r.transform;
                while (t != null && t != root.transform.parent)
                {
                    var p = PathUtil.RelativePath(root.transform, t);
                    if (facts.MaxAbsScale.TryGetValue(p, out var s)) scaleMul *= s;
                    else scaleMul *= MaxAbs(t.localScale);
                    t = t.parent;
                }

                list.Add(new RendererRef
                {
                    Renderer = r,
                    Mesh = mesh,
                    IsSkinned = skinned,
                    SharedMaterials = r.sharedMaterials ?? Array.Empty<Material>(),
                    UvChannelCount = MeshUvUtil.ChannelCount(mesh),
                    MaxScaleMul = Mathf.Max(1e-4f, scaleMul),
                    EnabledOrAnimatedOn = true,
                    Path = path
                });
            }
            AtoLog.Info($"Renderers eligible: {list.Count}");
            return list;
        }

        private static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        private static List<TextureBinding> CollectBindings(List<RendererRef> renderers, AnimationFacts facts,
            HashSet<Texture2D> whitelist, AvatarTextureOptimizer settings, AtoReportData report)
        {
            var list = new List<TextureBinding>();
            foreach (var rr in renderers)
            {
                var mats = rr.SharedMaterials;
                for (int slot = 0; slot < mats.Length; slot++)
                {
                    var extras = new List<Material>();
                    if (mats[slot] != null) extras.Add(mats[slot]);
                    var path = rr.Path;
                    foreach (var kv in facts.PathSlotMaterials)
                    {
                        if (kv.Key.StartsWith(path + "#", StringComparison.Ordinal) || kv.Key.Contains(path))
                            foreach (var m in kv.Value) if (m != null && !extras.Contains(m)) extras.Add(m);
                    }

                    foreach (var mat in extras)
                    {
                        var analysis = ShaderAnalyzeService.Analyze(mat);
                        if (!analysis.Supported)
                        {
                            var msg = AtoLoc.T(settings.language, "ato.warn.shaderUnknown", mat.shader != null ? mat.shader.name : "?", mat.name);
                            report.Warnings.Add(msg);
                            ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.NonFatal, "ato.warn.shaderUnknown", mat.shader, mat);
                            foreach (var tex in EnumerateMaterialTextures(mat))
                                whitelist.Add(tex);
                            continue;
                        }

                        var alpha = analysis.AlphaMode;
                        var cutoff = analysis.Cutoff;
                        foreach (var slotInfo in analysis.Slots)
                        {
                            var tex = mat.GetTexture(slotInfo.PropertyName) as Texture2D;
                            if (tex == null) continue;
                            bool eligible = !slotInfo.HasUnsafeTransform && slotInfo.Usage != TextureUsageKind.SpecialDeforming;
                            string reason = slotInfo.UnsafeReason;
                            if (facts.StAnimated.Count > 0)
                            {
                                foreach (var st in facts.StAnimated)
                                {
                                    if (st.Contains(slotInfo.PropertyName))
                                    {
                                        eligible = false;
                                        reason = "animated ST/scroll";
                                    }
                                }
                            }
                            bool wl = whitelist.Contains(tex);
                            var b = new TextureBinding
                            {
                                Texture = tex,
                                Material = mat,
                                Owner = rr,
                                SlotIndex = slot,
                                PropertyName = slotInfo.PropertyName,
                                Usage = slotInfo.Usage,
                                UvChannel = slotInfo.UvChannel,
                                AlphaMode = alpha,
                                Cutoff = cutoff,
                                ColorSpace = slotInfo.ImpliedColorSpace,
                                FilterMode = tex.filterMode,
                                IsWhitelisted = wl,
                                SkipAtlas = wl,
                                Eligible = eligible && !wl,
                                IneligibleReason = wl ? "whitelist" : reason
                            };
                            if (!b.Eligible)
                            {
                                var msg = AtoLoc.T(settings.language, "ato.warn.unsafeSlot", mat.name, slotInfo.PropertyName, b.IneligibleReason ?? "");
                                report.Warnings.Add(msg);
                                ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.Information, "ato.warn.unsafeSlot", mat, slotInfo.PropertyName, b.IneligibleReason ?? "");
                                whitelist.Add(tex);
                                b.IsWhitelisted = true;
                                b.SkipAtlas = true;
                            }
                            list.Add(b);

                            // Animation extra textures for this property join the UV group. / 动画切换的贴图并入原 UV 组。
                            foreach (var kv in facts.MaterialPropTextures)
                            {
                                if (!kv.Key.Contains(slotInfo.PropertyName)) continue;
                                foreach (var extra in kv.Value)
                                {
                                    if (extra == null || extra == tex) continue;
                                    list.Add(new TextureBinding
                                    {
                                        Texture = extra,
                                        Material = mat,
                                        Owner = rr,
                                        SlotIndex = slot,
                                        PropertyName = slotInfo.PropertyName,
                                        Usage = slotInfo.Usage,
                                        UvChannel = slotInfo.UvChannel,
                                        AlphaMode = alpha,
                                        Cutoff = cutoff,
                                        ColorSpace = slotInfo.ImpliedColorSpace,
                                        FilterMode = extra.filterMode,
                                        IsWhitelisted = whitelist.Contains(extra),
                                        SkipAtlas = whitelist.Contains(extra),
                                        Eligible = eligible && !whitelist.Contains(extra)
                                    });
                                }
                            }
                        }
                    }
                }
            }
            AtoLog.Info($"Texture bindings: {list.Count}");
            report.SourceTextures = new HashSet<Texture2D>(list.Select(b => b.Texture)).Count;
            return list;
        }

        private static IEnumerable<Texture2D> EnumerateMaterialTextures(Material mat)
        {
            var sh = mat.shader;
            for (int i = 0; i < sh.GetPropertyCount(); i++)
            {
                if (sh.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                if (mat.GetTexture(sh.GetPropertyName(i)) is Texture2D t) yield return t;
            }
        }

        private static List<UvGroup> BuildUvGroups(List<TextureBinding> bindings, AvatarTextureOptimizer settings, AtoReportData report)
        {
            var uf = new Dictionary<Texture2D, Texture2D>();
            Texture2D Find(Texture2D t)
            {
                if (t == null) return null;
                if (!uf.ContainsKey(t)) uf[t] = t;
                var p = t;
                while (uf[p] != p) p = uf[p] = uf[uf[p]];
                return p;
            }
            void Union(Texture2D a, Texture2D b)
            {
                a = Find(a); b = Find(b);
                if (a != null && b != null && a != b) uf[b] = a;
            }

            // Same mesh+uv channel → same UV group. Same source texture → same group.
            // 同一网格+UV 通道、同一原贴图 → 同一组。
            var byMeshUv = new Dictionary<(Mesh, int), List<Texture2D>>();
            foreach (var b in bindings)
            {
                if (b.Texture == null) continue;
                Find(b.Texture);
                var key = (b.Owner.Mesh, b.UvChannel);
                if (!byMeshUv.TryGetValue(key, out var l)) { l = new List<Texture2D>(); byMeshUv[key] = l; }
                l.Add(b.Texture);
            }
            foreach (var l in byMeshUv.Values)
                for (int i = 1; i < l.Count; i++) Union(l[0], l[i]);

            var groups = new Dictionary<Texture2D, UvGroup>();
            int id = 0;
            foreach (var b in bindings)
            {
                if (b.Texture == null) continue;
                var rootT = Find(b.Texture);
                if (!groups.TryGetValue(rootT, out var g))
                {
                    g = new UvGroup { Id = id++ };
                    groups[rootT] = g;
                }
                g.Textures.Add(b.Texture);
                g.Bindings.Add(b);
                if (b.IsWhitelisted) g.Whitelisted = true;
                if (b.SkipAtlas || !b.Eligible) g.SkipAtlas = true;
            }

            foreach (var g in groups.Values)
            {
                if (g.Whitelisted) continue;
                var seen = new HashSet<(Mesh, int)>();
                foreach (var b in g.Bindings)
                {
                    var k = (b.Owner.Mesh, b.UvChannel);
                    if (!seen.Add(k)) continue;
                    string warn;
                    var islands = UvIslandExtractor.Extract(b.Owner, b.UvChannel, b.Texture, out warn);
                    if (!string.IsNullOrEmpty(warn))
                    {
                        var msg = AtoLoc.T(settings.language, "ato.warn.uvWrap", b.Texture.name);
                        report.Warnings.Add(msg);
                        ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.NonFatal, "ato.warn.uvWrap", b.Texture);
                        g.Whitelisted = true;
                        g.SkipAtlas = true;
                        break;
                    }
                    g.Islands.AddRange(islands);
                }
            }

            AtoLog.Info($"UV groups: {groups.Count} islands: {groups.Values.Sum(g => g.Islands.Count)}");
            report.IslandCount = groups.Values.Sum(g => g.Islands.Count);
            return groups.Values.ToList();
        }

        private static void ScaleIslands(List<UvGroup> groups, AvatarTextureOptimizer settings, QualityParameters q, AtoReportData report)
        {
            bool near = settings.qualityPreset == QualityPreset.NearLossless || q.IsNearLossless;
            float minD = (int)settings.minPixelDensity;
            float maxD = (int)settings.maxPixelDensity;

            foreach (var g in groups)
            {
                if (g.Whitelisted) continue;
                // Barrel: take max required size among textures in the UV group. / 木桶效应取最大。
                foreach (var isl in g.Islands)
                {
                    Vector2 worst = Vector2.zero;
                    foreach (var b in g.Bindings)
                    {
                        if (b.Texture == null || !b.Eligible) continue;
                        var px = TextureDecodeCache.GetPixels(b.Texture, out var tw, out var th);
                        int x0 = Mathf.Clamp(Mathf.FloorToInt(isl.UvMin.x * tw), 0, tw - 1);
                        int y0 = Mathf.Clamp(Mathf.FloorToInt(isl.UvMin.y * th), 0, th - 1);
                        int iw = Math.Max(1, isl.OrigPixelW);
                        int ih = Math.Max(1, isl.OrigPixelH);
                        var crop = new Color32[iw * ih];
                        for (int y = 0; y < ih; y++)
                        for (int x = 0; x < iw; x++)
                        {
                            int sx = Mathf.Clamp(x0 + x, 0, tw - 1);
                            int sy = Mathf.Clamp(y0 + y, 0, th - 1);
                            crop[y * iw + x] = px[sy * tw + sx];
                        }

                        if (!near && isl.SolidColor && b.Usage != TextureUsageKind.Normal)
                        {
                            int m = Math.Min(4, Math.Min(iw, ih));
                            isl.Scale = new Vector2(m / (float)iw, m / (float)ih);
                            continue;
                        }

                        float worldLen = Mathf.Sqrt(Mathf.Max(isl.WorldArea, 1e-12f));
                        int densMin = Mathf.Clamp(Mathf.RoundToInt(minD * worldLen), 1, Math.Max(iw, ih));
                        int densMax = Mathf.Clamp(Mathf.RoundToInt(maxD * worldLen), densMin, Math.Max(iw, ih));
                        // Clamp by original island pixel size. / 受原岛物理像素钳制。
                        densMax = Math.Min(densMax, Math.Min(iw, ih));

                        var sc = QualityEval.FindScale(crop, iw, ih, q, b.Usage, b.AlphaMode, b.Cutoff, near, 1, densMin, densMax);
                        worst.x = Mathf.Max(worst.x, sc.x);
                        worst.y = Mathf.Max(worst.y, sc.y);
                    }
                    if (near) isl.Scale = Vector2.one;
                    else
                    {
                        isl.Scale.x = Mathf.Max(isl.Scale.x, worst.x);
                        isl.Scale.y = Mathf.Max(isl.Scale.y, worst.y);
                        // Not larger than original. / 不大于原尺寸。
                        isl.Scale.x = Mathf.Min(1f, isl.Scale.x);
                        isl.Scale.y = Mathf.Min(1f, isl.Scale.y);
                    }
                }
            }
        }

        private static Dictionary<int, AtlasResult> PackAtlases(List<UvGroup> groups, AvatarTextureOptimizer settings,
            AtoBuildPlatform platform, PlatformTextureSettings plat, string folder, BuildContext ctx, AtoReportData report)
        {
            var atlases = new Dictionary<int, AtlasResult>();
            int maxSide = platform == AtoBuildPlatform.PC ? 8192 : 4096;
            var pool = AtlasPacker.BuildPool(settings.experimentalNpot, maxSide);
            int atlasId = 0;

            // Type groups. / 类型组。
            var typeMap = new Dictionary<TypeGroupKey, List<UvGroup>>();
            foreach (var g in groups)
            {
                if (g.Whitelisted || g.SkipAtlas || g.Islands.Count == 0) continue;
                var key = MakeTypeKey(g);
                if (!typeMap.TryGetValue(key, out var l)) { l = new List<UvGroup>(); typeMap[key] = l; }
                l.Add(g);
            }

            foreach (var kv in typeMap)
            {
                var queue = kv.Value.OrderByDescending(g => g.Islands.Sum(i => i.Shape != null ? i.Shape.CountBits() : i.OrigPixelW * i.OrigPixelH)).ToList();
                var leftover = new List<UvGroup>(queue);
                while (leftover.Count > 0)
                {
                    // Try entire leftover in smallest candidate. / 尝试整队装进最小候选。
                    var allIslands = leftover.SelectMany(g => g.Islands).ToList();
                    long need = allIslands.Sum(i => (long)Mathf.Max(1, i.OrigPixelW * i.Scale.x) * Mathf.Max(1, i.OrigPixelH * i.Scale.y));
                    bool packedAll = false;
                    foreach (var cand in pool)
                    {
                        if ((long)cand.x * cand.y < need) continue;
                        int pad = AtlasPacker.PaddingFor(Math.Max(cand.x, cand.y), (int)settings.minPadding);
                        var places = new List<AtlasPacker.Place>();
                        if (!AtlasPacker.TryPack(allIslands, cand.x, cand.y, pad, places)) continue;
                        EmitAtlas(ref atlasId, leftover, allIslands, places, cand, kv.Key, folder, ctx, atlases, report);
                        leftover.Clear();
                        packedAll = true;
                        break;
                    }
                    if (packedAll) continue;

                    // Split: first-fit skip. / 装不下则跳过装不下的组。
                    var current = new List<UvGroup>();
                    var skip = new List<UvGroup>();
                    var maxCand = new Vector2Int(maxSide, maxSide);
                    foreach (var g in leftover)
                    {
                        var trial = new List<UvGroup>(current) { g };
                        var isl = trial.SelectMany(x => x.Islands).ToList();
                        int pad = AtlasPacker.PaddingFor(maxSide, (int)settings.minPadding);
                        var places = new List<AtlasPacker.Place>();
                        if (AtlasPacker.TryPack(isl, maxCand.x, maxCand.y, pad, places))
                            current.Add(g);
                        else if (current.Count == 0)
                        {
                            var msg = AtoLoc.T(settings.language, "ato.warn.atlasOverflow", g.Textures.First().name);
                            report.Warnings.Add(msg);
                            ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.NonFatal, "ato.warn.atlasOverflow", g.Textures.First());
                            g.SkipAtlas = true;
                            AtoLog.Warn(msg);
                        }
                        else skip.Add(g);
                    }
                    if (current.Count > 0)
                    {
                        var isl = current.SelectMany(x => x.Islands).ToList();
                        long n2 = isl.Sum(i => (long)Mathf.Max(1, i.OrigPixelW * i.Scale.x) * Mathf.Max(1, i.OrigPixelH * i.Scale.y));
                        foreach (var cand in pool)
                        {
                            if ((long)cand.x * cand.y < n2) continue;
                            int pad = AtlasPacker.PaddingFor(Math.Max(cand.x, cand.y), (int)settings.minPadding);
                            var places = new List<AtlasPacker.Place>();
                            if (!AtlasPacker.TryPack(isl, cand.x, cand.y, pad, places)) continue;
                            EmitAtlas(ref atlasId, current, isl, places, cand, kv.Key, folder, ctx, atlases, report);
                            break;
                        }
                    }
                    leftover = skip;
                }
            }

            report.AtlasCount = atlases.Count;
            AtoLog.Info($"Atlases generated: {atlases.Count}");
            return atlases;
        }

        private static TypeGroupKey MakeTypeKey(UvGroup g)
        {
            bool n = false, m = false;
            var usage = TextureUsageKind.Albedo;
            ColorSpace cs = ColorSpace.Gamma;
            FilterMode f = FilterMode.Bilinear;
            foreach (var b in g.Bindings)
            {
                if (b.Usage == TextureUsageKind.Normal) n = true;
                if (b.Usage == TextureUsageKind.Mask || b.Usage == TextureUsageKind.Gray) m = true;
                if (b.Usage == TextureUsageKind.Normal) usage = TextureUsageKind.Normal;
                else if (usage != TextureUsageKind.Normal) usage = b.Usage;
                cs = b.ColorSpace;
                if (b.Texture != null) f = b.Texture.filterMode;
            }
            // If a texture is used with and without normal, join the has-normal group. / 同时被有/无法线材质引用则归有法线组。
            return new TypeGroupKey { HasNormal = n, HasMask = m, ColorSpace = cs, Filter = f, PrimaryUsage = usage };
        }

        private static void EmitAtlas(ref int atlasId, List<UvGroup> groups, List<UvIsland> islands,
            List<AtlasPacker.Place> places, Vector2Int size, TypeGroupKey key, string folder, BuildContext ctx,
            Dictionary<int, AtlasResult> atlases, AtoReportData report)
        {
            // One atlas texture per unique source texture usage in this pack, sharing layout.
            // 同一布局下每种源贴图一张图集。
            var sources = new HashSet<Texture2D>();
            foreach (var g in groups) foreach (var t in g.Textures) sources.Add(t);

            foreach (var src in sources)
            {
                var usage = TextureUsageKind.Albedo;
                bool alpha = false;
                FilterMode filter = src.filterMode;
                ColorSpace cs = ColorSpace.Gamma;
                foreach (var g in groups)
                foreach (var b in g.Bindings)
                    if (b.Texture == src)
                    {
                        usage = b.Usage;
                        alpha |= b.AlphaMode != AlphaEvalMode.Opaque;
                        filter = b.FilterMode;
                        cs = b.ColorSpace;
                    }

                var tex = AtlasCompositor.Compose(size.x, size.y, islands, places, src, usage, alpha, filter, cs);
                tex.name = AvatarTextureOptimizer.AtlasNamePrefix + src.name + "_" + atlasId;
                var path = folder + "/" + tex.name + ".png";
                File.WriteAllBytes(path, tex.EncodeToPNG());
                ctx.AssetSaver.SaveAsset(tex);
                ObjectRegistry.RegisterReplacedObject(src, tex);

                var bits = islands.Sum(i => i.Shape != null ? i.Shape.CountBits() * 16 : i.PackedW * i.PackedH);
                float util = bits / (float)Math.Max(1, size.x * size.y);
                var ar = new AtlasResult
                {
                    Id = atlasId,
                    Width = size.x,
                    Height = size.y,
                    Texture = tex,
                    Key = key,
                    HasAlpha = alpha,
                    Usage = usage,
                    Filter = filter,
                    ColorSpace = cs,
                    Utilization = util
                };
                ar.Islands.AddRange(islands);
                ar.Sources.Add(src);
                atlases[atlasId] = ar;
                foreach (var isl in islands) isl.AtlasId = atlasId;
                report.Details.Add($"Atlas {tex.name} {size.x}x{size.y} util={util:P1} sources={src.name} islands={islands.Count}");
                AtoLog.Info($"Atlas {tex.name} {size.x}x{size.y} utilization {util:P1} from {src.name} islands={islands.Count}");
                atlasId++;
            }
        }

        private static void ScaleWholeTextures(List<TextureBinding> bindings, AvatarTextureOptimizer settings,
            QualityParameters q, PlatformTextureSettings plat, string folder, BuildContext ctx, AtoReportData report)
        {
            var done = new HashSet<Texture2D>();
            foreach (var b in bindings)
            {
                if (b.Texture == null || b.IsWhitelisted || !b.Eligible) continue;
                if (!done.Add(b.Texture)) continue;
                var px = TextureDecodeCache.GetPixels(b.Texture, out var w, out var h);
                bool near = settings.qualityPreset == QualityPreset.NearLossless || q.IsNearLossless;
                var sc = near ? Vector2.one : QualityEval.FindScale(px, w, h, q, b.Usage, b.AlphaMode, b.Cutoff, near, 4, 0, Math.Max(w, h));
                int nw = Math.Max(1, Mathf.RoundToInt(w * sc.x));
                int nh = Math.Max(1, Mathf.RoundToInt(h * sc.y));
                if (nw == w && nh == h) continue;
                var down = QualityEval.Downsample(px, w, h, nw, nh, b.AlphaMode != AlphaEvalMode.Opaque);
                var tex = new Texture2D(nw, nh, TextureFormat.RGBA32, true, b.ColorSpace == ColorSpace.Linear);
                tex.name = AvatarTextureOptimizer.AtlasNamePrefix + b.Texture.name;
                tex.wrapMode = b.Texture.wrapMode;
                tex.filterMode = b.Texture.filterMode;
                tex.SetPixels32(down);
                tex.Apply(true, false);
                var path = folder + "/" + tex.name + ".png";
                File.WriteAllBytes(path, tex.EncodeToPNG());
                ctx.AssetSaver.SaveAsset(tex);
                ObjectRegistry.RegisterReplacedObject(b.Texture, tex);
                foreach (var bb in bindings) if (bb.Texture == b.Texture) bb.Texture = tex;
                report.Details.Add($"Scaled {b.Texture.name} {w}x{h} -> {nw}x{nh}");
            }
        }

        private static void EmitReport(AvatarTextureOptimizer settings, AtoReportData report)
        {
            float saved = report.VramBefore <= 0 ? 0 : (1f - report.VramAfter / (float)Math.Max(1, report.VramBefore)) * 100f;
            var summary = AtoLoc.T(settings.language, "ato.report.summary",
                report.SourceTextures, report.OutputTextures, report.AtlasCount, report.IslandCount,
                EditorUtility.FormatBytes(report.VramBefore), EditorUtility.FormatBytes(report.VramAfter), saved);
            AtoLog.Info(summary);
            ErrorReport.ReportError(AtoLoc.NdmfLocalizer, ErrorSeverity.Information, "ato.report.summary",
                report.SourceTextures, report.OutputTextures, report.AtlasCount, report.IslandCount,
                EditorUtility.FormatBytes(report.VramBefore), EditorUtility.FormatBytes(report.VramAfter), saved);
            var sb = new StringBuilder();
            sb.AppendLine(AtoLoc.T("ato.report.title", settings.language));
            sb.AppendLine(summary);
            foreach (var d in report.Details) sb.AppendLine("  " + d);
            foreach (var w in report.Warnings) sb.AppendLine("  WARN " + w);
            AtoLog.Info(sb.ToString());
        }
    }
}
