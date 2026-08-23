using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Pipeline orchestrator. Stages (progress + cancellation between and inside stages):
    /// scan → analyze/map → pre-dedup → islands+quality → type groups → packing → atlas build →
    /// rewrite (mesh/material/animation) → post-dedup → save+report.
    /// / 主编排器。阶段：扫描→分析映射→预去重→岛+质量→类型组→装箱→图集构建→重写（网格/材质/动画）→
    /// 后置去重→保存并报告。全流程带进度与取消。
    /// </summary>
    internal static class ATOProcessor
    {
        internal static void Run(BuildContext ctx, AvatarTextureOptimizer component, AtoSettings settings,
            AtoPlatform platform)
        {
            var report = new ATOReport();
            var root = ctx.AvatarRootObject;
            var asc = ctx.Extension<AnimatorServicesContext>();

            using var store = new TextureStore();
            using var evaluator = new QualityEvaluator();
            var whole = new WholeTextureOptimizer(evaluator, settings, platform);

            // ================================================================== 1. scan
            List<RendererInfo> renderers;
            AnimationScanner anim;
            using (ATOLog.Stage("scan"))
            {
                renderers = RendererScanner.Scan(root);
                anim = new AnimationScanner(root);
                anim.Scan(asc, renderers);

                foreach (var info in renderers)
                {
                    if (!info.include && !anim.PossiblyEnabledRenderers.Contains(info.renderer))
                        continue;
                    info.include = true;
                    info.animAreaFactor = RendererScanner.ComputeAreaFactor(info, anim.MaxAnimScale);
                    info.slotAnimated = new bool[Mathf.Max(info.slots.Length, info.slotSwapMaterials.Count)];
                    foreach (var kv in info.slotSwapMaterials)
                        if (kv.Key < info.slotAnimated.Length)
                            info.slotAnimated[kv.Key] = true;
                }

                renderers = renderers.Where(r => r.include).ToList();
                report.RendererCount = renderers.Count;
                ATOLog.Info($"scan: {renderers.Count} renderers included / 参与处理的渲染器");
            }

            var whitelist = WhitelistScanner.CollectWhitelistTextures(component.whitelist);
            report.WhitelistCount = whitelist.Count;

            // ================================================================== 2. analyze & map
            var groups = new Dictionary<(Mesh, int), UvGroup>();
            var groupExtractions = new Dictionary<UvGroup, UVIslandExtractor.Extraction>();
            var strictestCategories = new Dictionary<Texture2D, HashSet<TexCategory>>();
            var textureAlpha = new Dictionary<Texture2D, HashSet<(AlphaMode, float)>>();
            var seenTextures = new HashSet<Texture2D>();

            using (ATOLog.Stage("analyze"))
            {
                foreach (var info in renderers)
                {
                    ATOProgress.Report(0.05f, "Analyze", info.renderer.name);
                    ProcessRendererMaterials(info, groups, whitelist, strictestCategories, textureAlpha,
                        seenTextures, report);
                }

                // island extraction per group (meshes shared across renderers → same group object)
                // 每组提取岛（共享网格共享组对象）
                foreach (var g in groups.Values)
                {
                    var ex = UVIslandExtractor.Extract(g.mesh, g.channel, g.primaryRenderer, g.areaFactor);
                    groupExtractions[g] = ex;
                    if (!ex.Group.atlasEligible)
                    {
                        g.atlasEligible = false;
                        g.ineligibleReason = ex.Group.ineligibleReason;
                    }
                }
            }

            report.UvGroupCount = groups.Count;

            // propagate usage categories into groups / 用途类别并入组
            foreach (var g in groups.Values)
            {
                foreach (var tex in g.textures.Keys.ToList())
                {
                    if (strictestCategories.TryGetValue(tex, out var cats))
                        g.usageCategories[tex] = new HashSet<TexCategory>(cats);
                    // storage category: normal &gt; color &gt; mask / 存储类别优先级
                    g.textures[tex] = StorageCategory(g.usageCategories.TryGetValue(tex, out var c) && c != null
                        ? c
                        : new HashSet<TexCategory> { g.textures[tex] });
                }
            }

            // ================================================================== 3. pre-dedup
            Dictionary<Texture2D, Texture2D> dedupMap;
            using (ATOLog.Stage("dedup"))
            {
                dedupMap = store.Dedup(seenTextures);
                foreach (var kv in dedupMap)
                    if (whitelist.Contains(kv.Value))
                        whitelist.Add(kv.Key); // whitelisted canonical ⇒ duplicates too / 白名单传导
                report.DedupCount = dedupMap.Count;
                ATOLog.Info($"pre-dedup: {dedupMap.Count} duplicates / 去重 {dedupMap.Count} 个");
            }

            // whitelist propagation to groups / 白名单传导到组
            foreach (var g in groups.Values)
            {
                foreach (var tex in g.textures.Keys)
                {
                    if (!whitelist.Contains(tex)) continue;
                    if (g.atlasEligible)
                    {
                        g.atlasEligible = false;
                        g.ineligibleReason = $"whitelisted texture '{tex.name}' in group / 组内含白名单贴图";
                    }
                }
            }

            // ================================================================== 4. islands + quality
            var instances = new Dictionary<(UvGroup, UvIsland, Texture2D), IslandInstance>();
            var noAtlasTextures = new Dictionary<Texture2D, (TexCategory storage, bool srgb)>();
            using (ATOLog.Stage("quality"))
            {
                if (!settings.generateAtlas)
                {
                    // no-atlas mode: every used non-whitelisted texture gets whole-texture scaling
                    // 无图集模式：所有使用中的非白名单贴图整图缩放
                    foreach (var tex in seenTextures.Where(t => !whitelist.Contains(t)))
                        CollectWholeTarget(tex, strictestCategories, textureAlpha, groups, store, whole);
                }
                else
                {
                    var scaler = new IslandScaler();
                    var eligible = groups.Values.Where(g => g.atlasEligible).ToList();
                    int done = 0;
                    foreach (var g in eligible)
                    {
                        ATOProgress.Report(0.1f + 0.3f * done / Mathf.Max(1, eligible.Count),
                            "Quality scaling", $"{g.mesh.name} ch{g.channel}");
                        done++;
                        scaler.ScaleGroup(g, store, evaluator, settings,
                            (i, n) => ATOProgress.Report(0.1f + 0.3f * (done - 1 + (float)i / Mathf.Max(1, n)) / Mathf.Max(1, eligible.Count),
                                "Quality scaling", $"{g.mesh.name}:{i}/{n}"));
                    }

                    foreach (var inst in scaler.Instances)
                        instances[(GroupOf(inst), inst.island, inst.texture)] = inst;

                    // textures of non-eligible groups → whole-texture path / 非可图集组的贴图走整图
                    foreach (var g in groups.Values.Where(g => !g.atlasEligible))
                        foreach (var tex in g.textures.Keys.Where(t => !whitelist.Contains(t)))
                            CollectWholeTarget(tex, strictestCategories, textureAlpha, groups, store, whole);

                    report.IslandCount = eligible.Sum(g => g.islands.Count);
                    report.AtlasEligibleGroups = eligible.Count;
                }

                report.InstanceCount = instances.Count;
                report.TextureCount = seenTextures.Count;
            }

            // ================================================================== 5+6. type groups & packing
            var placements = new Dictionary<(UvGroup, UvIsland), PlacedIsland>();
            var atlasOf = new Dictionary<(UvGroup, Texture2D), BuiltAtlas>();
            var layouts = new List<AtlasLayout>();
            if (settings.generateAtlas)
            {
                using (ATOLog.Stage("packing"))
                {
                    var packer = new AtlasPacker();
                    foreach (var inst in instances.Values)
                        packer.SetFinalSize(GroupOf(inst), inst.island, inst.finalW, inst.finalH);

                    var typeGroups = TypeGroup.Build(groups.Values, store);
                    int ti = 0;
                    foreach (var tg in typeGroups)
                    {
                        ti++;
                        ATOProgress.Report(0.45f, "Packing", tg.Key);
                        var queue = tg.Groups
                            .Select(g => (g, RasterArea(g, instances)))
                            .OrderByDescending(q => q.Item2)
                            .ToList();

                        var result = packer.Pack(tg, queue, settings.experimentalNpot,
                            Mathf.Clamp(settings.maxAtlasSize, 64, platform == AtoPlatform.PC ? 8192 : 4096),
                            settings.minPadding, _ => { });

                        foreach (var layout in result.Atlases)
                        {
                            layouts.Add(layout);
                            foreach (var p in layout.Placed)
                                placements[(p.Group, p.Island)] = p;
                        }

                        foreach (var (failedGroup, reason) in result.Failed)
                        {
                            failedGroup.atlasEligible = false;
                            failedGroup.ineligibleReason = reason;
                            report.Warnings.Add($"group '{failedGroup.mesh.name}' ch{failedGroup.channel}: {reason}");
                            foreach (var tex in failedGroup.textures.Keys.Where(t => !whitelist.Contains(t)))
                                CollectWholeTarget(tex, strictestCategories, textureAlpha, groups, store, whole);
                        }

                        ATOLog.Info($"type group {tg.Key}: {tg.Groups.Count} groups → {result.Atlases.Count} atlases / 类型组建集");
                    }
                }
            }

            // ================================================================== 7. atlas build
            using (ATOLog.Stage("atlas"))
            {
                var builder = new AtlasBuilder(settings, platform);
                foreach (var layout in layouts)
                {
                    ATOProgress.Report(0.6f, "Building atlases", $"{layout.Width}x{layout.Height}");
                    builder.Build(layout, instances);
                }

                foreach (var atlas in builder.Atlases)
                {
                    report.AtlasLines.Add(
                        $"{atlas.Name}: {atlas.Width}x{atlas.Height} {atlas.Texture.format}, " +
                        $"util {atlas.Utilization:P0}, {atlas.Kind}" +
                        (atlas.MirrorDownscaleShift > 0 ? $" ×2^-{atlas.MirrorDownscaleShift}" : "") +
                        $", sources: {string.Join(", ", atlas.Sources.Select(s => s.name).Distinct().Take(12))}");
                }

                report.Warnings.AddRange(builder.Warnings);

                // map (group, texture) → atlas / 建立映射
                foreach (var atlas in builder.Atlases)
                {
                    foreach (var p in atlas.Layout.Placed)
                    {
                        foreach (var tex in p.Group.textures.Keys)
                        {
                            var cat = p.Group.textures[tex];
                            var kind = cat == TexCategory.Normal ? AtlasKind.NormalAux
                                : (cat == TexCategory.Mask || cat == TexCategory.Grayscale) ? AtlasKind.LinearAux
                                : AtlasKind.Primary;
                            if (kind != atlas.Kind) continue;
                            atlasOf[(p.Group, tex)] = atlas;
                        }
                    }
                }

                // whole-texture results / 整图结果
                foreach (var kv in whole.Replacements)
                    if (kv.Value != kv.Key)
                        report.AtlasLines.Add($"ATO_T: '{kv.Key.name}' → {kv.Value.width}x{kv.Value.height} {kv.Value.format}");
            }

            // ================================================================== 8. rewrite
            var meshRewriter = new MeshRewriter(placements);
            var rewriter = new MaterialRewriter(root, anim.FactsByPath, groups, atlasOf, dedupMap, whole, settings);
            using (ATOLog.Stage("rewrite"))
            {
                ATOProgress.Report(0.8f, "Rewriting meshes");
                if (settings.generateAtlas)
                {
                    foreach (var g in groups.Values.Where(g => g.atlasEligible))
                    {
                        var mesh = meshRewriter.Rewrite(g);
                        foreach (var info in renderers.Where(r => r.mesh == g.mesh))
                        {
                            if (g.primaryRenderer != null && info.renderer == g.primaryRenderer.renderer) { }
                            AssignMesh(info, mesh);
                        }
                    }
                }

                ATOProgress.Report(0.85f, "Rewriting materials & animations");
                rewriter.Apply(asc);
                rewriter.PostDedup(asc, renderers);
                report.Warnings.AddRange(meshRewriter.Warnings);
            }

            // ================================================================== 9. save + report
            using (ATOLog.Stage("save"))
            {
                ATOProgress.Report(0.95f, "Saving assets");
                var toSave = new List<Object>();
                foreach (var atlas in atlasOf.Values.Distinct()) toSave.Add(atlas.Texture);
                foreach (var kv in whole.Replacements)
                    if (kv.Value != kv.Key) toSave.Add(kv.Value);
                foreach (var m in rewriter.GeneratedMaterials) toSave.Add(m);
                foreach (var mesh in meshRewriter.GeneratedMeshes) toSave.Add(mesh);
                foreach (var mesh in rewriter.GeneratedMeshes) toSave.Add(mesh);
                ctx.AssetSaver.SaveAssets(toSave);
            }

            using (ATOLog.Stage("report"))
            {
                ComputeSavings(groups, atlasOf, whole, whitelist, seenTextures, report, store);
                report.Warnings.AddRange(DistinctWarnings(report));
                report.Emit(ATOL10n.NdmfLocalizer);
                ATOLog.Info("done / 完成: " + report.Summary());
            }
        }

        // ------------------------------------------------------------------ helpers
        private static void AssignMesh(RendererInfo info, Mesh mesh)
        {
            if (mesh == null || info.mesh == mesh) return;
            info.mesh = mesh;
            if (info.smr != null) info.smr.sharedMesh = mesh;
            else
            {
                var mf = info.renderer.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = mesh;
            }
        }

        private static UvGroup GroupOf(IslandInstance inst) => FindGroupCache(inst);

        private static UvGroup FindGroupCache(IslandInstance inst)
        {
            // island → group via stored field / 岛持有组引用
            return inst.island.Group;
        }

        private static TexCategory StorageCategory(HashSet<TexCategory> cats)
        {
            if (cats.Contains(TexCategory.Normal)) return TexCategory.Normal;
            if (cats.Contains(TexCategory.Color)) return TexCategory.Color;
            if (cats.Contains(TexCategory.LinearColor)) return TexCategory.LinearColor;
            if (cats.Contains(TexCategory.Grayscale)) return TexCategory.Grayscale;
            return TexCategory.Mask;
        }

        private static long RasterArea(UvGroup g,
            Dictionary<(UvGroup, UvIsland, Texture2D), IslandInstance> instances)
        {
            // approximate rasterized area from island bounds (px²) / 以岛包围盒近似光栅面积
            long area = 0;
            foreach (var island in g.islands)
            {
                int w = 4, h = 4;
                foreach (var tex in g.textures.Keys)
                {
                    var inst = instances.TryGetValue((g, island, tex), out var i) ? i : null;
                    if (inst != null) { w = Mathf.Max(w, inst.finalW); h = Mathf.Max(h, inst.finalH); break; }
                }
                area += (long)w * h;
            }
            return area;
        }

        private static void CollectWholeTarget(Texture2D tex,
            Dictionary<Texture2D, HashSet<TexCategory>> strictestCategories,
            Dictionary<Texture2D, HashSet<(AlphaMode, float)>> textureAlpha,
            Dictionary<(Mesh, int), UvGroup> groups, TextureStore store, WholeTextureOptimizer whole)
        {
            var cats = strictestCategories.TryGetValue(tex, out var c) && c.Count > 0 ? c : new HashSet<TexCategory> { TexCategory.Color };
            var storage = StorageCategory(cats);
            var info = store.GetImportInfo(tex);
            var alpha = textureAlpha.TryGetValue(tex, out var a) ? a.ToList() : new List<(AlphaMode, float)>();
            if (alpha.Count == 0) alpha.Add((AlphaMode.Opaque, 0.5f));
            whole.Optimize(tex, storage, storage == TexCategory.Color && info.sRGB, alpha, store);
        }

        private static void ComputeSavings(Dictionary<(Mesh, int), UvGroup> groups,
            Dictionary<(UvGroup, Texture2D), BuiltAtlas> atlasOf, WholeTextureOptimizer whole,
            HashSet<Texture2D> whitelist, HashSet<Texture2D> seen, ATOReport report, TextureStore store)
        {
            long origPx = 0;
            float origMb = 0;
            foreach (var tex in seen)
            {
                origPx += (long)tex.width * tex.height;
                origMb += (long)tex.width * tex.height * TextureFormats.BytesPerPixel(tex.format);
            }

            long newPx = 0;
            float newMb = 0;
            var counted = new HashSet<Texture2D>();
            foreach (var atlas in atlasOf.Values.Distinct())
            {
                if (!counted.Add(atlas.Texture)) continue;
                newPx += (long)atlas.Width * atlas.Height;
                newMb += (long)atlas.Width * atlas.Height * TextureFormats.BytesPerPixel(atlas.Texture.format);
            }
            foreach (var kv in whole.Replacements)
            {
                if (kv.Value == kv.Key || !counted.Add(kv.Value)) continue;
                newPx += (long)kv.Value.width * kv.Value.height;
                newMb += (long)kv.Value.width * kv.Value.height * TextureFormats.BytesPerPixel(kv.Value.format);
            }
            // untouched textures still exist / 未触碰的贴图仍计入成品
            foreach (var tex in seen)
            {
                if (atlasOf.Values.Any(a => a.Sources.Contains(tex))) continue;
                if (whole.Replacements.TryGetValue(tex, out var rep) && rep != tex) continue;
                newPx += (long)tex.width * tex.height;
                newMb += (long)tex.width * tex.height * TextureFormats.BytesPerPixel(tex.format);
            }

            report.OriginalPixels = origPx;
            report.OptimizedPixels = newPx;
            report.OriginalMegabytes = origMb / (1024f * 1024f);
            report.OptimizedMegabytes = newMb / (1024f * 1024f);
        }

        private static IEnumerable<string> DistinctWarnings(ATOReport report) =>
            report.Warnings.Distinct().Take(200);

        // ------------------------------------------------------------------ per-renderer analysis
        private static void ProcessRendererMaterials(RendererInfo info,
            Dictionary<(Mesh, int), UvGroup> groups, HashSet<Texture2D> whitelist,
            Dictionary<Texture2D, HashSet<TexCategory>> strictestCategories,
            Dictionary<Texture2D, HashSet<(AlphaMode, float)>> textureAlpha,
            HashSet<Texture2D> seen, ATOReport report)
        {
            var allMaterials = new List<Material>();
            for (int i = 0; i < info.slots.Length; i++)
            {
                if (info.slots[i] != null) allMaterials.Add(info.slots[i]);
                if (info.slotSwapMaterials.TryGetValue(i, out var swaps))
                    allMaterials.AddRange(swaps.Where(m => m != null));
            }

            foreach (var mat in allMaterials.Distinct())
            {
                var analysis = ShaderAnalyzer.Analyze(mat);
                if (analysis.unknown)
                {
                    foreach (var p in mat.GetTexturePropertyNames())
                        if (mat.GetTexture(p) is Texture2D t)
                        {
                            whitelist.Add(t);
                            report.Warnings.Add(
                                $"material '{mat.name}': {analysis.unknownReason}; textures whitelisted / 材质无法分析，贴图白名单");
                        }
                    continue;
                }

                foreach (var slot in analysis.slots)
                {
                    var tex = slot.texture;
                    seen.Add(tex);

                    if (info.unsafeAnimatedProps.Contains(slot.property))
                    {
                        whitelist.Add(tex);
                        report.Warnings.Add($"texture '{tex.name}': animated transform '{slot.property}' → whitelist / 动画变换白名单");
                        continue;
                    }

                    if (!slot.safe || slot.uvChannel < 0)
                    {
                        whitelist.Add(tex);
                        report.Warnings.Add(
                            $"texture '{tex.name}' ({slot.property}): {slot.unsafeReason ?? "non-mesh UV"} → whitelist / 白名单");
                        continue;
                    }

                    // register usage / 登记用途
                    if (!strictestCategories.TryGetValue(tex, out var cats))
                        strictestCategories[tex] = cats = new HashSet<TexCategory>();
                    cats.Add(slot.category);

                    if (!textureAlpha.TryGetValue(tex, out var alphaSet))
                        textureAlpha[tex] = alphaSet = new HashSet<(AlphaMode, float)>();
                    alphaSet.UnionWith(analysis.alphaCandidates);
                    alphaSet.UnionWith(info.animatedAlpha);

                    var key = (info.mesh, slot.uvChannel);
                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = new UvGroup
                        {
                            mesh = info.mesh,
                            channel = slot.uvChannel,
                            primaryRenderer = info,
                        };
                        groups[key] = group;
                    }
                    else
                    {
                        group.areaFactor = Mathf.Max(group.areaFactor, info.animAreaFactor);
                    }
                    group.areaFactor = Mathf.Max(group.areaFactor, info.animAreaFactor);
                    if (group.primaryRenderer == null) group.primaryRenderer = info;
                    if (!group.textures.ContainsKey(tex)) group.textures[tex] = slot.category;
                    group.alphaCandidates.UnionWith(analysis.alphaCandidates);
                    group.alphaCandidates.UnionWith(info.animatedAlpha);
                }
            }

            // texture swap animations: map swapped textures onto the group of that property
            // 贴图切换动画：把切换贴图并入该属性的组
            foreach (var (prop, textures) in info.textureSwaps)
            {
                int channel = -1;
                foreach (var m in info.slots)
                {
                    if (m == null) continue;
                    var slot = ShaderAnalyzer.Analyze(m).slots.FirstOrDefault(s => s.property == prop);
                    if (slot != null) { channel = slot.uvChannel; break; }
                }
                if (channel < 0) channel = 0;

                foreach (var tex in textures)
                {
                    seen.Add(tex);
                    if (whitelist.Contains(tex)) continue;
                    var key = (info.mesh, channel);
                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = new UvGroup { mesh = info.mesh, channel = channel, primaryRenderer = info };
                        groups[key] = group;
                    }
                    if (!group.textures.ContainsKey(tex)) group.textures[tex] = TexCategory.Color;
                    if (!strictestCategories.TryGetValue(tex, out var cats))
                        strictestCategories[tex] = cats = new HashSet<TexCategory>();
                    cats.Add(TexCategory.Color);
                }
            }
        }
    }
}
