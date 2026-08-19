// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// ATOPlugin.cs — NDMF 插件入口与主管线 / NDMF plugin entry & main pipeline
//
// 时序: MA 执行后、AAO 执行前（Transforming 阶段 + AfterPlugin(MA) + BeforePlugin(AAO)）。
// 管线阶段（每阶段计时/进度/可取消）:
//   Validate → Analyze(动画→渲染器/材质/贴图/UV组) → Dedup → Islands → Scale →
//   [图集: Pack → Build] → [整图缩放(兜底/关闭图集)] → Persist/Import →
//   MeshRewrite(AAO 兼容) → MaterialPatch → AnimationPatch → FinalDedup → RemoveSelf → Report
// 取消: 保留磁盘临时资产，释放 CPU/GPU/内存。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;
using api = net.fosa.avatar_texture_optimizer.editor.api;

[assembly: ExportsPlugin(typeof(net.fosa.avatar_texture_optimizer.editor.ATOPlugin))]

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// ATO NDMF 插件 / ATO NDMF plugin.
    /// </summary>
    public sealed class ATOPlugin : Plugin<ATOPlugin>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run(new ATOMainPass());
        }
    }

    /// <summary>
    /// 主 Pass / Main pass.
    /// </summary>
    public sealed class ATOMainPass : Pass<ATOMainPass>
    {
        protected override void Execute(BuildContext ctx)
        {
            new ATOPipeline(ctx).Run();
        }
    }

    /// <summary>
    /// 管线 / Pipeline.
    /// </summary>
    public sealed class ATOPipeline
    {
        private readonly BuildContext _ctx;
        private readonly GameObject _root;
        private ATOComponent _cfg;
        private readonly ATOReport _report = new ATOReport();
        private TextureDecodeCache _cache;

        public ATOPipeline(BuildContext ctx)
        {
            _ctx = ctx;
            _root = ctx.AvatarRootObject;
        }

        public void Run()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Cancel.Reset();

            try
            {
                // ---- Validate ----
                BeginStage(I18n.T("stage.validate"));
                if (!Validate(out _cfg))
                {
                    _report.cancelled = false;
                    return; // 未挂载组件 → 静默跳过 / no component → skip silently
                }
                Log.Verbose = _cfg.verboseLogging;
                I18n.UserChoice = LanguageFor(_cfg.language);
                _report.avatarName = _root.name;

                ATOPlatform platform = _cfg.DefaultPlatform();
                bool atlasing = _cfg.generateAtlases;
                bool npot = _cfg.experimentalNpot;
                int maxAtlasSize = MaxAtlasSize(_cfg, platform);

                // ---- Analyze ----
                BeginStage(I18n.T("stage.analyze"));
                var anim = AnimationAnalyzer.Analyze(_root, new ShaderAnalyzer.LogContext { avatarName = _root.name });
                var wl = WhitelistResolver.Resolve(_cfg.whitelist);
                var analysis = AvatarAnalyzer.Analyze(_root, _cfg, anim, wl);

                // ---- Dedup ----
                BeginStage(I18n.T("stage.dedup"));
                _cache = new TextureDecodeCache();
                ApplyTextureDedup(analysis, wl);

                // ---- Islands ----
                BeginStage(I18n.T("stage.islands"));
                if (atlasing)
                {
                    var renderersByMesh = new Dictionary<Mesh, List<Renderer>>();
                    foreach (var slot in analysis.slots)
                    {
                        if (slot.mesh == null) continue;
                        if (!renderersByMesh.TryGetValue(slot.mesh, out var list))
                        {
                            list = new List<Renderer>();
                            renderersByMesh[slot.mesh] = list;
                        }
                        if (!list.Contains(slot.renderer)) list.Add(slot.renderer);
                    }
                    foreach (var group in analysis.allGroups)
                    {
                        Cancel.Checkpoint();
                        if (group.whitelisted) continue;
                        renderersByMesh.TryGetValue(group.mesh, out var users);
                        IslandExtractor.ExtractGroup(group, _cfg, anim, users ?? new List<Renderer>());
                    }
                }

                // ---- Scale ----
                BeginStage(I18n.T("stage.scale"));
                var scaler = new ScalerContext(_cfg, _cache, anim);
                foreach (var group in analysis.allGroups)
                {
                    Cancel.Checkpoint();
                    if (group.whitelisted) continue;
                    IslandScaler.ScaleGroup(group, scaler);
                }
                // 计算 hasAlpha / category（透明判定）/ compute hasAlpha & category
                foreach (var tref in analysis.allTextures)
                {
                    if (tref.whitelisted || tref.source == null) continue;
                    tref.hasAlpha = _cache.UsesAlpha(tref.source, tref.sRGB);
                    if (tref.role == TextureRole.MainColor && tref.hasAlpha)
                    {
                        tref.category = TextureCategory.Transparent;
                    }
                }

                // ---- Pack & Build atlases ----
                PackOutcome outcome = null;
                if (atlasing)
                {
                    BeginStage(I18n.T("stage.pack"));
                    outcome = AtlasPacker.Pack(analysis, _cfg, maxAtlasSize, npot);
                    BeginStage(I18n.T("stage.build"));
                    AtlasBuilder.BuildAll(outcome, _cache, _cfg.paddingOption);
                }

                // ---- Whole-texture scaling (fallback / atlasing off) ----
                var scaledTextures = new Dictionary<Texture2D, Texture2D>();
                if (!atlasing)
                {
                    Log.VerboseLog("Atlasing disabled; scaling whole textures.");
                    scaledTextures = ScaledTextureBuilder.Build(analysis.allTextures, scaler);
                }
                else if (outcome != null && outcome.fallbackGroups.Count > 0)
                {
                    var fallbackTexs = new List<TextureRef>();
                    foreach (var g in outcome.fallbackGroups)
                    {
                        fallbackTexs.AddRange(g.textures.Where(t => !t.whitelisted));
                    }
                    scaledTextures = ScaledTextureBuilder.Build(fallbackTexs, scaler);
                }

                // 白名单组（非"跨缝"原因）内的非白名单贴图：整图缩放（UV 未重排，整图缩放安全）/
                // textures of whitelisted groups (except OOB-cross-seam) get whole-texture scaling
                if (atlasing && outcome != null)
                {
                    var extra = new List<TextureRef>();
                    foreach (var g in analysis.allGroups)
                    {
                        if (!g.whitelisted) continue;
                        if (g.whitelistReason == "oob-cross-seam") continue;
                        foreach (var t in g.textures)
                        {
                            if (!t.whitelisted && t.source != null && !scaledTextures.ContainsKey(t.source))
                            {
                                extra.Add(t);
                            }
                        }
                    }
                    if (extra.Count > 0)
                    {
                        var more = ScaledTextureBuilder.Build(extra, scaler);
                        foreach (var kv in more) scaledTextures[kv.Key] = kv.Value;
                    }
                }

                // ---- Persist & import settings ----
                BeginStage(I18n.T("stage.import"));
                var generated = new List<GeneratedTexture>();
                if (outcome != null)
                {
                    foreach (var family in outcome.families.Values)
                    {
                        foreach (var atlas in family.atlases)
                        {
                            if (atlas.texture == null) continue;
                            generated.Add(new GeneratedTexture
                            {
                                texture = atlas.texture,
                                category = AtlasCategory(atlas),
                                hasAlpha = AtlasHasAlpha(atlas),
                                sRGB = family.sRGB,
                                filterMode = family.filterMode,
                                aniso = 1,
                                label = $"{family.role}_{atlas.width}x{atlas.height}",
                            });
                        }
                    }
                }
                foreach (var kv in scaledTextures)
                {
                    var tref = analysis.allTextures.FirstOrDefault(t => t.source == kv.Key);
                    if (tref == null) continue;
                    generated.Add(new GeneratedTexture
                    {
                        texture = kv.Value,
                        category = tref.category,
                        hasAlpha = tref.hasAlpha,
                        sRGB = tref.sRGB,
                        filterMode = tref.filterMode,
                        aniso = 1,
                        label = tref.source.name,
                    });
                }
                var persisted = ImportSettingsApplier.PersistAndConfigure(generated, _root.name, _cfg, platform);

                // 持久化前计算内存贴图像素哈希（导入后 isReadable=false 无法再读）/
                // precompute in-memory pixel hashes (imported assets are not readable)
                var persistedHashes = new Dictionary<Texture2D, string>();
                foreach (var kv in persisted)
                {
                    try
                    {
                        persistedHashes[kv.Value] = FinalDeduper.QuickHash(kv.Key.GetPixels32());
                    }
                    catch (System.Exception) { }
                }

                // 更新内存贴图 → 持久化贴图 / update in-memory → persistent
                if (outcome != null)
                {
                    foreach (var family in outcome.families.Values)
                    {
                        foreach (var atlas in family.atlases)
                        {
                            if (atlas.texture != null && persisted.TryGetValue(atlas.texture, out var p))
                            {
                                atlas.texture = p;
                            }
                        }
                    }
                }
                var scaledPersisted = new Dictionary<Texture2D, Texture2D>();
                foreach (var kv in scaledTextures)
                {
                    if (persisted.TryGetValue(kv.Value, out var p)) scaledPersisted[kv.Key] = p;
                    else scaledPersisted[kv.Key] = kv.Value;
                }

                // ---- Mesh rewrite ----
                BeginStage(I18n.T("stage.rewrite"));
                var meshResult = MeshRewriter.Rewrite(analysis, _root);
                MeshRewriter.ApplyAaoCompatibility(meshResult, analysis);

                // ---- Material patch ----
                BeginStage(I18n.T("stage.patch"));
                var matResult = MaterialPatcher.Patch(analysis, outcome ?? new PackOutcome(), scaledPersisted, anim);

                // ---- Animation patch ----
                var clipPatches = AnimationPatcher.Patch(_root, anim, matResult, _ctx);

                // ---- Final dedup & slot merge ----
                BeginStage(I18n.T("stage.finalize"));
                var dedupResult = FinalDeduper.Run(analysis, matResult, meshResult, persisted, _root, anim, _ctx,
                    persistedHashes);

                // ---- Remove self from build output ----
                RemoveSelf();

                // ---- 释放内存中的生成贴图（引用已全部切到持久化资产）/
                // release in-memory generated textures (all refs now use persistent assets)
                foreach (var g in generated)
                {
                    if (g.texture != null) Object.DestroyImmediate(g.texture);
                }
                foreach (var kv in scaledTextures)
                {
                    if (kv.Value != null) Object.DestroyImmediate(kv.Value);
                }

                // ---- Report ----
                BeginStage(I18n.T("stage.report"));
                FillReport(analysis, outcome, scaledPersisted, meshResult, clipPatches, dedupResult, sw);
                _report.Write();

                EndStage(I18n.T("stage.report"));
            }
            catch (ATOCancelException)
            {
                _report.cancelled = true;
                Log.Info(I18n.T("report.cancelled"));
                Log.Info("Temporary assets preserved on disk; CPU/GPU/memory released.");
            }
            catch (Exception e)
            {
                Log.Error($"AvatarTextureOptimizer failed: {e}");
                throw; // 交由 NDMF 错误处理 / let NDMF handle it
            }
            finally
            {
                _cache?.Dispose();
                Cancel.Clear();
                EndStage("");
                Resources.UnloadUnusedAssets();
            }
        }

        // ------------------------------------------------------------------
        // 校验 / Validation
        // ------------------------------------------------------------------
        private bool Validate(out ATOComponent cfg)
        {
            cfg = null;

            var components = _root.GetComponentsInChildren<ATOComponent>(true);
            if (components.Length == 0)
            {
                Log.VerboseLog("No ATO component found; skipping avatar.");
                return false;
            }

            if (components.Length > 1)
            {
                var err = new ATOError("errors.multipleComponents", ErrorSeverity.Error, _root.name);
                err.AddReference(_ctx.ObjectRegistry.GetReference(components[1].gameObject));
                ErrorReport.ReportError(err);
                return false;
            }

            var comp = components[0];
            if (comp.GetComponent<VRCAvatarDescriptor>() == null)
            {
                var err = new ATOError("errors.invalidPlacement", ErrorSeverity.Error, comp.gameObject.name);
                err.AddReference(_ctx.ObjectRegistry.GetReference(comp.gameObject));
                ErrorReport.ReportError(err);
                return false;
            }

            cfg = comp;
            return true;
        }

        private static string LanguageFor(ATOLanguage lang)
        {
            switch (lang)
            {
                case ATOLanguage.English: return "en";
                case ATOLanguage.ChineseSimplified: return "zh-CN";
                default: return "auto";
            }
        }

        private static int MaxAtlasSize(ATOComponent cfg, ATOPlatform platform)
        {
            int def = platform == ATOPlatform.PC ? 8192 : 4096;
            if (cfg.platformOverrideEnabled)
            {
                var ov = cfg.OverrideFor(platform);
                if (ov != null && ov.maxAtlasSize > 0) def = ov.maxAtlasSize;
            }
            return def;
        }

        // ------------------------------------------------------------------
        // 贴图去重应用 / apply texture dedup
        // ------------------------------------------------------------------
        private void ApplyTextureDedup(AvatarAnalysis analysis, Whitelist wl)
        {
            var all = analysis.allTextures.Select(t => t.source).Distinct().ToList();
            var dedup = TextureDeduper.Deduplicate(all, tex => wl.IsWhitelisted(tex), _cache);
            if (dedup.RemovedCount == 0) return;

            foreach (var tref in analysis.allTextures)
            {
                if (tref.source != null && dedup.map.TryGetValue(tref.source, out var canonical))
                {
                    tref.source = canonical;
                    if (dedup.whitelistedCanonicals.Contains(canonical))
                    {
                        tref.whitelisted = true;
                        tref.whitelistReason = "whitelisted-via-dedup";
                    }
                }
            }

            // 材质与动画中的引用在补丁阶段统一处理（dedup 映射并入纹理映射）/
            // material/clip references are rewired at patch time
            Log.Info($"texture dedup: removed {dedup.RemovedCount} duplicate textures");
        }

        // ------------------------------------------------------------------
        // 图集辅助 / atlas helpers
        // ------------------------------------------------------------------
        private static TextureCategory AtlasCategory(AtlasResult atlas)
        {
            if (atlas.family.category == TextureCategory.Transparent) return TextureCategory.Transparent;
            return AtlasHasAlpha(atlas) ? TextureCategory.Transparent : atlas.family.category;
        }

        private static bool AtlasHasAlpha(AtlasResult atlas)
        {
            foreach (var kv in atlas.content)
            {
                if (kv.Key.hasAlpha) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // 阶段包装（进度 + 第三方钩子）/ stage wrapper (progress + third-party hooks)
        // ------------------------------------------------------------------
        private void BeginStage(string name)
        {
            foreach (var h in api.ATOPublicAPI.PipelineHooks) h.OnStageBegin(name);
            Cancel.Tick(name, 0f);
            Log.BeginStage(name);
        }

        private void EndStage(string name)
        {
            foreach (var h in api.ATOPublicAPI.PipelineHooks) h.OnStageEnd(name);
            Log.EndStage(name);
        }

        private void RemoveSelf()
        {
            var comp = _root.GetComponentInChildren<ATOComponent>(true);
            if (comp != null)
            {
                Object.DestroyImmediate(comp);
                Log.VerboseLog("Removed ATO component from build output.");
            }
        }

        // ------------------------------------------------------------------
        // 报告填充 / fill report
        // ------------------------------------------------------------------
        private void FillReport(AvatarAnalysis analysis, PackOutcome outcome,
            Dictionary<Texture2D, Texture2D> scaledTextures, MeshRewriteResult meshResult,
            int clipPatches, FinalDedupResult dedupResult, System.Diagnostics.Stopwatch sw)
        {
            _report.durationMs = sw.ElapsedMilliseconds;
            _report.slotCount = analysis.processedSlotCount;
            _report.whitelistedSlotCount = analysis.whitelistedSlotCount;
            _report.inputTextures = analysis.allTextures.Count;
            _report.dedupedTextures = analysis.allTextures.Count; // 简化统计
            _report.optimizedTextures = analysis.allTextures.Count(t => !t.whitelisted);
            _report.whitelistedTextures = analysis.allTextures.Count(t => t.whitelisted);

            foreach (var group in analysis.allGroups)
            {
                _report.islandDetected += group.islands?.Count ?? 0;
                _report.islandPacked += group.islands?.Count(i => i.packed) ?? 0;
                _report.islandScaled += group.islands?.Count ?? 0;
            }

            if (outcome != null)
            {
                int idx = 0;
                foreach (var family in outcome.families.Values)
                {
                    foreach (var atlas in family.atlases)
                    {
                        _report.atlasCount++;
                        _report.AddAtlasLine(ReportFormat.AtlasLine(atlas, idx++));
                        _report.sourceBytes += atlas.sourcePixels * 4;
                        _report.targetBytes += atlas.targetPixels * 4;
                    }
                }
            }
            foreach (var kv in scaledTextures)
            {
                if (kv.Value != null)
                {
                    _report.sourceBytes += (long)kv.Key.width * kv.Key.height * 4;
                    _report.targetBytes += (long)kv.Value.width * kv.Value.height * 4;
                }
            }

            _report.meshRewrites = meshResult.rewrittenCount;
            _report.meshChannels = meshResult.channelsSummary;
            _report.clipPatches = clipPatches;
            _report.materialsDeduped = dedupResult.materialsRemoved;
            _report.texturesDeduped = dedupResult.texturesRemoved;
        }
    }
}
