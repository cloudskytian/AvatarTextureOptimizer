// Avatar Texture Optimizer / 头像贴图优化器
// The main build pipeline orchestrator. Validates configuration, runs all
// stages with progress + cancellation, produces the report, removes itself.
// 主构建管线编排器。校验配置、按进度+取消运行全部阶段、产出报告、移除自身组件。
//
// Coverage invariants (Coder consensus, module 12):
//   - A group whose islands failed to build (islands.Count == 0) is a HARD
//     whitelist: neither atlased nor whole-texture rewritten.
//   - A group that has islands + optimizable textures but was not covered by
//     any atlas plan (packing failure / AAO block) is whole-texture scaled.
//   - Whole-texture scaling at ratio ~1 leaves the source untouched (fail-open).
// 覆盖不变量（第 12 轮 Coder 共识）：
//   - 岛构建失败（岛数为 0）的组=硬白名单：不进图集也不整图重写。
//   - 有岛且有可优化贴图、但未被任何图集规划覆盖的组（装箱失败/AAO 阻塞）走整图缩放。
//   - 整图缩放比例≈1 时不改动源贴图（fail-open 语义）。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// End-to-end ATO pipeline for one avatar build.
    /// 单次 Avatar 构建的端到端 ATO 管线。
    /// </summary>
    public sealed class ATOPipeline : IDisposable
    {
        private readonly BuildContext _ctx;
        private ATOProgress _progress;
        private ATORtPool _rtPool;
        private ATOGpuPipeline _gpu;
        private ATOAssetWriter _writer;
        private ATOUsageModel _model;
        private ATOAnimationData _anim;
        private AvatarTextureOptimizer _settings;
        private ATOPlatform _platform = ATOConsts.DefaultPlatform;
        private string _buildId;
        private Dictionary<ATOUVGroup, Dictionary<ATOIsland, Vector2>> _lastQuality;
        // Stage timings recorded before the model (and its report) exists.
        // 模型（及其报告）建立之前记录的阶段耗时。
        private readonly List<(string stage, long ms)> _earlyTimings = new List<(string, long)>();

        public ATOPipeline(BuildContext ctx)
        {
            _ctx = ctx;
        }

        public void Dispose()
        {
            _gpu?.Dispose();
            _gpu = null;
            _rtPool?.Dispose();
            _rtPool = null;
            _progress?.Dispose();
            _progress = null;
            _writer = null;
        }

        // ==================================================================
        // Validation / 校验
        // ==================================================================

        /// <summary>Validate the avatar &amp; component; returns false (with errors reported) when the build must abort. / 校验 Avatar 与组件；必须中止时返回 false（已上报错误）。</summary>
        public bool Validate(out AvatarTextureOptimizer component)
        {
            component = null;
            var root = _ctx.AvatarRootObject;
            var all = root.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (all.Length == 0) return false; // no component: plugin inert / 无组件：插件不生效
            if (all.Length > 1)
            {
                // Component rule: exactly ONE ATO component on the VRCAvatarDescriptor GameObject.
                // 组件规则：恰好一个 ATO 组件且必须挂在 VRCAvatarDescriptor 所在 GameObject 上。
                var abs = all.Where(c => c.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() != null).ToArray();
                ReportValidationError(abs.Length >= 1 ? "validate.multiple" : "validate.descriptor",
                    abs.Length >= 1 ? (Object)abs[0] : all[0]);
                return false;
            }
            component = all[0];
            var descriptor = component.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                ReportValidationError("validate.descriptor", component);
                return false;
            }
            return true;
        }

        private void ReportValidationError(string key, Object contextObj)
        {
            // Full text lives in i18n (NDMF SafeSubst only supports {0..9} on the
            // localized string itself), so no substitutions are passed here.
            // 全文放在 i18n（NDMF SafeSubst 只支持本地化串上的 {0..9} 占位），这里不传替换。
            var err = new ATOSimpleError(key, ErrorSeverity.Error);
            try
            {
                // Static entry: uses the active registry scope, falls back to a plain
                // reference when built outside one. (IObjectRegistry.GetReference is
                // explicitly implemented, so an instance call would not compile.)
                // 静态入口：使用活动注册表作用域，无作用域时回退为普通引用。
                // （IObjectRegistry.GetReference 是显式接口实现，实例调用无法编译。）
                if (contextObj != null)
                    err._references.Add(ObjectRegistry.GetReference(contextObj));
            }
            catch { /* referencing is best-effort / 引用仅尽力而为 */ }
            ErrorReport.ReportError(err);
        }

        // ==================================================================
        // Main entry / 主入口
        // ==================================================================

        /// <summary>Run the whole pipeline (after Validate passed). / 运行整条管线（在 Validate 通过后）。</summary>
        public void Run(AvatarTextureOptimizer component)
        {
            _settings = component;
            _buildId = DefaultRules.NewBuildId();
            _platform = ResolveBuildPlatform();

            ATOLoc.Configure(component.languageMode, component.manualLanguage);
            ATOLog.VerboseEnabled = component.verboseLogging;

            using (_progress = new ATOProgress(ATOLoc.T("ato:progress.title"), ""))
            using (_rtPool = new ATORtPool(1024L * 1024 * 1024))
            using (_gpu = new ATOGpuPipeline(_rtPool))
            {
                try
                {
                    Stage("ato:stage.cleanup", () => ATOAssetWriter.CleanStaleGenerated(_buildId), 0.02f);
                    Stage("ato:stage.animation", () => _anim = ATOAnimationScanner.Scan(_ctx), 0.06f);
                    Stage("ato:stage.model", BuildModel, 0.14f);
                    Stage("ato:stage.dedupTextures", () => ATOTextureDedup.Run(_model, _settings, _progress), 0.22f);
                    Stage("ato:stage.islands", BuildIslands, 0.30f);

                    Dictionary<ATOUVGroup, Dictionary<ATOIsland, Vector2>> quality = null;
                    Stage("ato:stage.quality", () =>
                    {
                        var evaluator = new ATOQualityEvaluator(_settings, _gpu, _progress);
                        quality = evaluator.EvaluateAll(_model.uvGroups, 0.30f, 0.55f);
                        _lastQuality = quality;
                    }, 0.55f);

                    _writer = new ATOAssetWriter(_settings, _model.report, _buildId, _platform);
                    _writer.BeginBatch();
                    try
                    {
                        var matRewriter = new ATOMaterialRewriter(_ctx, _model.report, _anim);
                        var assignments = new Dictionary<(Material, string), ATOTextureAssignment>();

                        if (_settings.generateAtlas)
                        {
                            AtlasPath(quality, matRewriter, assignments);
                        }
                        else
                        {
                            StandaloneOnlyPath(assignments);
                        }

                        // Materials & renderer slots / 材质与渲染器槽
                        Stage("ato:stage.materials", () =>
                        {
                            matRewriter.ApplyAssignments(assignments);
                            matRewriter.ApplyToRenderers(_model.renderers.Select(r => r.renderer));
                        }, 0.90f);

                        if (_settings.deduplicateMaterials)
                        {
                            Stage("ato:stage.materialDedup", () =>
                            {
                                var modes = new Dictionary<Material, ATORenderMode>();
                                foreach (var kv in matRewriter.Cloned)
                                    modes[kv.Value] = ATOShaderAnalyzer.ResolveRenderMode(kv.Value);
                                var dedup = new ATOMaterialDedup(_ctx, _model.report, _anim);
                                dedup.DeduplicateMaterials(matRewriter.Cloned.Values);
                                dedup.MergeDuplicateOpaqueSlots(modes);
                            }, 0.95f);
                        }
                    }
                    finally
                    {
                        _writer.EndBatch();
                    }

                    _model.report.originalTextureBytes = _model.textures.Values.Sum(t => t.sourceBytes);
                    Stage("ato:stage.report", FinishReport, 0.99f);
                    RemoveSelf(component);
                }
                catch (ATOCancelledException ce)
                {
                    // Full text is in i18n (ato:cancelled); no substitutions needed.
                    // 全文见 i18n（ato:cancelled），无需替换参数。
                    ATOLog.Warn(ATOLoc.T("ato:cancelled"));
                    ErrorReport.ReportError(new ATOSimpleError("cancelled", ErrorSeverity.NonFatal));
                    Debug.Log(ce.Message);
                    // On-disk temp assets are intentionally kept (requirement);
                    // CPU/GPU/memory were released by the using-scopes unwinding.
                    // 磁盘临时资产按要求保留；CPU/GPU/内存已随 using 作用域回退释放。
                }
                finally
                {
                    _model = null;
                }
            }
        }

        private ATOPlatform ResolveBuildPlatform()
        {
            // The component's platform overrides gate every optimization knob;
            // the active build target picks which override block applies.
            // 组件的平台覆盖门控所有优化参数；当前构建目标决定使用哪个覆盖块。
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return ATOPlatform.Android;
                case BuildTarget.iOS: return ATOPlatform.iOS;
                default: return ATOPlatform.PC;
            }
        }

        private void RemoveSelf(AvatarTextureOptimizer component)
        {
            try
            {
                if (component != null) Object.DestroyImmediate(component);
            }
            catch (Exception e)
            {
                ATOLog.Warn("self-remove failed: " + e.Message);
            }
        }

        private void RecordTiming(string stage, long ms)
        {
            if (_model != null) _model.report.stageTimings.Add((stage, ms));
            else _earlyTimings.Add((stage, ms));
        }

        private void FlushEarlyTimings()
        {
            if (_earlyTimings.Count == 0) return;
            _model.report.stageTimings.InsertRange(0, _earlyTimings);
            _earlyTimings.Clear();
        }

        private void Stage(string key, Action action, float progress)
        {
            _progress.ThrowIfCancelled();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _progress.Report(ATOLoc.T(key), progress);
            action();
            sw.Stop();
            RecordTiming(ATOLoc.T(key), sw.ElapsedMilliseconds);
            ATOLog.Verbose($"{ATOLoc.T(key)}: {sw.ElapsedMilliseconds} ms");
        }

        private T StageRet<T>(string key, Func<T> action, float progress)
        {
            _progress.ThrowIfCancelled();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _progress.Report(ATOLoc.T(key), progress);
            var r = action();
            sw.Stop();
            RecordTiming(ATOLoc.T(key), sw.ElapsedMilliseconds);
            return r;
        }

        private void BuildModel()
        {
            var builder = new ATOModelBuilder(_ctx, _settings, _anim);
            _model = builder.Build();
            FlushEarlyTimings();
            // Extension hook: handlers may still extend whitelists/exclusions here.
            // 扩展钩子：此处处理者仍可追加白名单/排除项。
            ATOExtensionApi.NotifyModelBuilt(_model);
        }

        private void BuildIslands()
        {
            foreach (var g in _model.uvGroups)
            {
                _progress.ThrowIfCancelled();
                var r = ATOIslands.Build(g);
                if (!r.ok)
                {
                    // Hard whitelist: group untouched by any path.
                    // 硬白名单：该组不被任何路径触碰。
                    g.SetAtlasBlocked(r.failureReason);
                    g.FinalDisposition = "whitelist: " + r.failureReason;
                    _model.report.whitelistNotes.Add(
                        $"[{ContextOf(g)}] {r.failureReason}");
                    ATOLog.Verbose($"islands blocked: {ContextOf(g)}: {r.failureReason}");
                }
                else if (g.islands.Count == 0)
                {
                    // Hard whitelist: no islands, untouched by any path.
                    // 硬白名单：无岛，不被任何路径触碰。
                    g.FinalDisposition = "whitelist: no islands / 无岛";
                }
                else
                {
                    _model.report.islandsTotal += g.islands.Count;
                }
            }
        }

        private static string ContextOf(ATOUVGroup g)
            => $"{(g.mesh != null ? g.mesh.name : "?")}#sm{g.submesh}#uv{g.uvChannel}";

        // ==================================================================
        // Atlas path / 图集路径
        // ==================================================================

        private void AtlasPath(
            Dictionary<ATOUVGroup, Dictionary<ATOIsland, Vector2>> quality,
            ATOMaterialRewriter matRewriter,
            Dictionary<(Material, string), ATOTextureAssignment> assignments)
        {
            // Pre-check AAO constraints on the atlas placement plan.
            // Fails closed: a group that cannot evacuate is atlas-blocked.
            // 对图集摆放预检 AAO 约束。保守失败：无法转移的组标记为图集阻塞。
            var evacPlan = new Dictionary<(SkinnedMeshRenderer, int), int>(); // (smr,ch)->evacChannel
            if (ATOAAOCompat.IsInstalled)
            {
                foreach (var group in _model.uvGroups)
                {
                    if (group.IsAtlasBlocked || group.islands.Count == 0) continue;
                    foreach (var smrUse in group.usages)
                    {
                        if (!(smrUse.renderer is SkinnedMeshRenderer smr)) continue;
                        int ch = group.uvChannel;
                        if (!ATOAAOCompat.IsTexCoordUsed(smr, ch)) continue;
                        if (evacPlan.ContainsKey((smr, ch))) continue;
                        if (!ATOAAOCompat.TryPickEvacuationChannel(smr, ch,
                                c => ChannelUsedByModel(_model, smr.sharedMesh, c), out int evacChannel))
                        {
                            group.SetAtlasBlocked(ATOLoc.T("ato:aao.nochannel", smr.name, ch));
                            _model.report.whitelistNotes.Add($"[{ContextOf(group)}] {group.AtlasBlockReason}");
                            break; // one blocked usage blocks the whole group / 一处阻塞即整组阻塞
                        }
                        evacPlan[(smr, ch)] = evacChannel;
                    }
                }
            }

            // Material-slot conflict guard (Reviewer R1-F2): a material slot can hold
            // exactly ONE texture. If the same (material, property) pair feeds two
            // different UV groups, atlasing either group (which rewrites that
            // mesh's UVs to atlas space) would corrupt the other group's sampling.
            // Therefore NONE of the conflicting groups may atlas; all fall back to
            // whole-texture scaling, which is UV-layout independent and consistent.
            // 材质槽冲突守卫（评审 R1-F2）：一个材质槽只能装一张贴图。同一
            // (材质, 属性) 对若喂给两个不同 UV 组，图集化其中任一组（重写该网格
            // UV 到图集空间）都会破坏另一组的采样。因此冲突组一律不进图集，
            // 全部回退为与 UV 布局无关的整图缩放。
            var slotGroups = new Dictionary<(Material, string), HashSet<ATOUVGroup>>();
            foreach (var g in _model.uvGroups)
            {
                foreach (var u in g.usages)
                {
                    if (!u.Optimizable || u.material == null || u.propertyName == null) continue;
                    if (!slotGroups.TryGetValue((u.material, u.propertyName), out var set))
                    {
                        set = new HashSet<ATOUVGroup>();
                        slotGroups[(u.material, u.propertyName)] = set;
                    }
                    set.Add(g);
                }
            }
            var conflicted = new HashSet<ATOUVGroup>();
            foreach (var kv in slotGroups)
            {
                if (kv.Value.Count <= 1) continue;
                foreach (var g in kv.Value) conflicted.Add(g);
            }
            if (conflicted.Count > 0)
            {
                _model.report.whitelistNotes.Add(ATOLoc.T("ato:atlas.slotconflict",
                    string.Join(", ", conflicted.Select(ContextOf).Take(8).ToArray())));
            }

            // Extension hook: last chance to atlas-block groups before planning.
            // 扩展钩子：规划前给组图集阻塞标记的最后机会。
            ATOExtensionApi.NotifyBeforeAtlasPlan(_model);

            // Plan / 规划
            var planOutcome = StageRet("ato:stage.atlasPlan", () =>
            {
                Func<ATOUVGroup, bool> eligible =
                    g => !g.IsAtlasBlocked && !conflicted.Contains(g) &&
                         g.islands.Count > 0 && g.OptimizableTextures().Any();
                var custom = ATOExtensionApi.CustomPacker;
                if (custom != null)
                {
                    try
                    {
                        var customPlans = custom(new ATOCustomPlanContext
                        {
                            Model = _model,
                            QualityRatios = quality,
                            Platform = _platform,
                            Settings = _settings,
                            IsGroupAtlasEligible = eligible,
                        }, out var customFallback);
                        if (customPlans != null)
                        {
                            return (customPlans, customFallback ?? new List<ATOUVGroup>());
                        }
                    }
                    catch (Exception e)
                    {
                        ATOLog.Warn("custom packer threw, using built-in: " + e.Message);
                    }
                }
                var builtin = new ATOAtlasPlanner(_settings, _platform).Plan(
                    _model.uvGroups, quality, eligible, out var builtinFallback);
                return (builtin, builtinFallback);
            }, 0.62f);
            var plans = planOutcome.Item1;
            var fallbackGroups = planOutcome.Item2;

            // Disposition tracing: every planned group is reported as "atlas".
            // 处置追踪：每个进入规划的组记录为 "atlas"。
            var groupOfIsland = new Dictionary<ATOIsland, ATOUVGroup>();
            foreach (var g in _model.uvGroups)
                foreach (var isl in g.islands) groupOfIsland[isl] = g;
            foreach (var plan in plans)
                foreach (var pi in plan.islands)
                    if (groupOfIsland.TryGetValue(pi.island, out var g))
                        g.FinalDisposition = "atlas";
            foreach (var g in fallbackGroups)
                if (g != null) g.FinalDisposition = "standalone:atlas-fallback";

            // Compose + write / 合成与写盘
            var composer = new ATOAtlasComposer(_gpu, _settings);
            var generatedSets = new Dictionary<ATOAtlasPlan, ATOAtlasSetResult>();
            var writerResults = new Dictionary<(ATOAtlasPlan, ATORole), ATOWrittenTexture>();
            int planIndex = 0;
            var composeSw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var plan in plans)
            {
                _progress.ThrowIfCancelled();
                planIndex++;
                _progress.Report(ATOLoc.T("ato:stage.atlasCompose"),
                    0.62f + 0.18f * planIndex / Mathf.Max(1, plans.Count),
                    plan.typeGroupKey);
                var set = composer.Compose(plan, quality, _progress);
                generatedSets[plan] = set;
                foreach (var kv in set.layers)
                {
                    string name = $"{ATOConsts.AtlasPrefix}{ShortHash(plan.typeGroupKey)}_{plan.setIndex}_{kv.Key}";
                    var category = ATOCategoryClassifier.ForLayer(kv.Key, kv.Value.hasAlpha);
                    var filter = HighestFilterMode(plan);
                    var written = _writer.Write(name, kv.Value, category, filter, out _);
                    writerResults[(plan, kv.Key)] = written;
                    _model.report.atlases.Add(new ATOBuildReport.AtlasInfo
                    {
                        name = name,
                        width = written.width,
                        height = written.height,
                        islandCount = plan.islands.Count,
                        textureCount = plan.sourceTextures.Count,
                        utilization = plan.utilization,
                        typeGroupKey = plan.typeGroupKey,
                        sourceBytes = plan.sourceTextures.Sum(t => t.sourceBytes),
                        resultBytes = written.bytes,
                    });
                }
                _model.report.islandsAtlased += plan.islands.Count;
            }
            composeSw.Stop();
            RecordTiming(ATOLoc.T("ato:stage.atlasCompose"), composeSw.ElapsedMilliseconds);
            _model.report.optimizedTextureBytes += writerResults.Values.Sum(w => w.bytes);

            // Mesh UV rewrite / 网格 UV 重写
            var placementsByGroup = BuildPlacementLookup(plans);
            var meshRewriter = new ATOMeshRewriter(_ctx, _model.report);
            Stage("ato:stage.meshRewrite", () =>
            {
                foreach (var rec in _model.renderers)
                {
                    var renderer = rec.renderer;
                    var mesh = rec.mesh;
                    var list = GroupPlacementsForMesh(mesh, placementsByGroup);
                    if (list.Count == 0) continue;

                    Mesh baseMesh = mesh;
                    if (renderer is SkinnedMeshRenderer smr)
                    {
                        // AAO evacuation must capture ORIGINAL channel data first.
                        // AAO 通道转移必须先取原通道数据。
                        var channels = new HashSet<int>(list.Select(t => t.group.uvChannel));
                        bool evacFailed = false;
                        foreach (var ch in channels)
                        {
                            if (!evacPlan.TryGetValue((smr, ch), out int evacChannel)) continue;

                            Mesh evacuated = meshRewriter.EnsureAaoEvacuation(
                                smr, baseMesh, ch,
                                c => ChannelUsedByModel(_model, mesh, c), evacChannel);
                            if (evacuated == null) { evacFailed = true; break; }
                            baseMesh = evacuated;
                        }
                        if (evacFailed)
                        {
                            // Fail-closed: keep this renderer's original mesh/materials.
                            // 保守失败：该渲染器维持原网格与材质。
                            _progress.ThrowIfCancelled();
                            continue;
                        }
                    }

                    var newMesh = meshRewriter.RewriteRendererMesh(renderer, baseMesh, list);
                    if (renderer is SkinnedMeshRenderer smr2) smr2.sharedMesh = newMesh;
                    else
                    {
                        var mf = renderer.GetComponent<MeshFilter>();
                        if (mf != null) mf.sharedMesh = newMesh;
                    }
                }
            }, 0.84f);

            // Material assignments from atlas layers / 来自图集层的材质分配
            foreach (var usage in _model.usages)
            {
                if (!usage.Optimizable) continue;
                var group = usage.GroupOf(_model);
                if (group == null) continue;
                if (!placementsByGroup.TryGetValue(group, out var pg)) continue;
                var plan = pg.plan;
                if (!generatedSets.TryGetValue(plan, out var set)) continue;
                var layerRole = ResolveLayerRole(set, usage.role);
                if (layerRole == null) continue;
                if (!writerResults.TryGetValue((plan, layerRole.Value), out var written)) continue;
                if (written.texture == null) continue;
                assignments[(usage.material, usage.propertyName)] = new ATOTextureAssignment
                {
                    replacement = written.texture,
                    usesAtlas = true,
                    atlasW = written.width,
                    atlasH = written.height,
                };
            }

            // Fallback: every group with usable islands that no atlas covered gets
            // whole-texture scaling (packing failures, AAO blocks, ...).
            // 兜底：凡是有可用岛但未被图集覆盖的组（装箱失败、AAO 阻塞等）整图缩放。
            var covered = new HashSet<ATOUVGroup>(placementsByGroup.Keys);
            var standaloneGroups = new List<ATOUVGroup>();
            foreach (var g in _model.uvGroups)
            {
                if (covered.Contains(g)) continue;
                if (g.islands.Count == 0) continue; // hard whitelist / 硬白名单
                if (!g.OptimizableTextures().Any()) continue;
                standaloneGroups.Add(g);
            }
            foreach (var g in fallbackGroups)
                if (!standaloneGroups.Contains(g) && g.islands.Count > 0 &&
                    g.OptimizableTextures().Any()) standaloneGroups.Add(g);
            _model.report.uvGroupsSkippedAtlas = standaloneGroups.Count;
            if (standaloneGroups.Count > 0)
                StandaloneScaleForGroups(standaloneGroups, assignments, "atlas-fallback");
        }

        private ATORole? ResolveLayerRole(ATOAtlasSetResult set, ATORole role)
        {
            if (set.layers.ContainsKey(role)) return role;
            // MainLayer & Emission fall back to the Main layer (same RGB layout).
            // MainLayer 与 Emission 回退 Main 层（RGB 布局一致）。
            if (role == ATORole.MainLayer || role == ATORole.Emission)
            {
                if (set.layers.ContainsKey(ATORole.Main)) return ATORole.Main;
            }
            return null;
        }

        private static FilterMode HighestFilterMode(ATOAtlasPlan plan)
        {
            var f = FilterMode.Point;
            foreach (var t in plan.sourceTextures)
                if ((int)t.filterMode > (int)f) f = t.filterMode;
            return f;
        }

        private static string ShortHash(string s)
        {
            unchecked
            {
                uint h = 5381;
                foreach (var c in s) h = h * 33 + c;
                return h.ToString("x6");
            }
        }

        /// <summary>Per-group placements: group -&gt; (plan, island-&gt;placement). / 每组摆放：组 -&gt; (规划, 岛-&gt;摆放)。</summary>
        private static Dictionary<ATOUVGroup, (ATOAtlasPlan plan, Dictionary<ATOIsland, ATOPlacedIsland> placements)> BuildPlacementLookup(List<ATOAtlasPlan> plans)
        {
            var dict = new Dictionary<ATOUVGroup, (ATOAtlasPlan, Dictionary<ATOIsland, ATOPlacedIsland>)>();
            foreach (var plan in plans)
            {
                foreach (var p in plan.islands)
                {
                    if (!dict.TryGetValue(p.unit.group, out var entry))
                    {
                        entry = (plan, new Dictionary<ATOIsland, ATOPlacedIsland>());
                        dict[p.unit.group] = entry;
                    }
                    entry.Item2[p.island] = p;
                }
            }
            return dict;
        }

        private static List<(ATOUVGroup group, Dictionary<ATOIsland, ATOPlacedIsland> placements, ATOAtlasPlacementLookup lookup)> GroupPlacementsForMesh(
            Mesh mesh,
            Dictionary<ATOUVGroup, (ATOAtlasPlan plan, Dictionary<ATOIsland, ATOPlacedIsland> placements)> placementsByGroup)
        {
            var list = new List<(ATOUVGroup, Dictionary<ATOIsland, ATOPlacedIsland>, ATOAtlasPlacementLookup)>();
            foreach (var kv in placementsByGroup)
            {
                var group = kv.Key;
                if (group.mesh != mesh) continue;
                list.Add((group, kv.Value.placements,
                    new ATOAtlasPlacementLookup { plan = kv.Value.plan, atlasW = kv.Value.plan.width, atlasH = kv.Value.plan.height }));
            }
            return list;
        }

        private static bool ChannelUsedByModel(ATOUsageModel model, Mesh mesh, int channel)
        {
            foreach (var g in model.uvGroups)
            {
                if (g.mesh == mesh && g.uvChannel == channel) return true;
            }
            return false;
        }

        // ==================================================================
        // Standalone (whole-texture) path / 整图（非图集）路径
        // ==================================================================

        private void StandaloneOnlyPath(
            Dictionary<(Material, string), ATOTextureAssignment> assignments)
        {
            // Only groups with usable islands take part; island-build failures stay
            // hard-whitelisted even in no-atlas mode.
            // 只有岛可用的组参与；岛构建失败的组即使在无图集模式下也保持硬白名单。
            var groups = _model.uvGroups
                .Where(g => g.islands.Count > 0 && g.OptimizableTextures().Any())
                .ToList();
            StandaloneScaleForGroups(groups, assignments, "no-atlas");
        }

        private void StandaloneScaleForGroups(
            List<ATOUVGroup> groups,
            Dictionary<(Material, string), ATOTextureAssignment> assignments,
            string reasonTag)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var textures = new HashSet<ATOTextureEntry>();
            foreach (var g in groups)
            {
                g.FinalDisposition = "standalone:" + reasonTag;
                foreach (var t in g.OptimizableTextures()) textures.Add(t);
            }
            var replaced = new Dictionary<ATOTextureEntry, ATOWrittenTexture>();
            _progress.Report(ATOLoc.T("ato:stage.standalone"), 0.86f);
            int done = 0;
            foreach (var tex in textures)
            {
                _progress.ThrowIfCancelled();
                done++;
                _progress.Report(ATOLoc.T("ato:stage.standalone"),
                    0.86f + 0.03f * done / Mathf.Max(1, textures.Count),
                    tex.texture != null ? tex.texture.name : "?");
                var written = ScaleWholeTexture(tex, reasonTag);
                if (written != null) replaced[tex] = written;
            }

            foreach (var g in groups)
            {
                foreach (var usage in g.usages)
                {
                    if (!usage.Optimizable) continue;
                    if (!replaced.TryGetValue(usage.texture, out var written) || written == null || written.texture == null)
                        continue;
                    assignments[(usage.material, usage.propertyName)] = new ATOTextureAssignment
                    {
                        replacement = written.texture,
                        usesAtlas = false,
                    };
                }
            }
            sw.Stop();
            RecordTiming(ATOLoc.T("ato:stage.standalone"), sw.ElapsedMilliseconds);
        }

        /// <summary>
        /// Scale one whole texture by its strictest island ratio and write it.
        /// Returns null when nothing needs doing (fail-open at ratio~1).
        /// 按最严格岛比例缩放整个贴图并写盘。无事可做（比例≈1 的 fail-open）时返回 null。
        /// </summary>
        private ATOWrittenTexture ScaleWholeTexture(ATOTextureEntry tex, string reasonTag)
        {
            using (new ATOLog.Step($"standalone:{(tex.texture != null ? tex.texture.name : "?")}"))
            {
                bool normalPath = tex.isNormalMap || tex.category == ATOTextureCategory.Normal;
                ATOTextureSession session = null;
                try
                {
                    session = _gpu.OpenSession(tex, normalPath);
                    float ratio = RequiredWholeTextureRatio(tex);
                    int tw = Mathf.Max(16, Mathf.RoundToInt(tex.width * ratio));
                    int th = Mathf.Max(16, Mathf.RoundToInt(tex.height * ratio));
                    if (ratio >= 0.999f && tw == tex.width && th == tex.height)
                    {
                        // Fail-open: keep the source asset byte-identical.
                        // 保守放行：源资产保持字节级不变。
                        return null;
                    }

                    var full = new RectInt(0, 0, tex.width, tex.height);
                    var chain = _gpu.DownsampleCrop(session.fullLinearFloat, full, tw, th);
                    RenderTexture finalRt = chain[chain.Count - 1];
                    RenderTexture bytesRt;
                    try
                    {
                        if (normalPath)
                        {
                            var renorm = _gpu.RunPass(finalRt, ATOGpuPipeline.PassRenormalize, tw, th);
                            chain.Add(renorm);
                            var enc = _gpu.Pool.Rent(tw, th, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                            chain.Add(enc);
                            _gpu.EncodeNormalToBytes(enc, renorm);
                            bytesRt = _gpu.Pool.Rent(tw, th, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                            chain.Add(bytesRt);
                            Graphics.Blit(enc, bytesRt);
                        }
                        else if (tex.sRGB)
                        {
                            bytesRt = _gpu.EncodeToDisplay(finalRt, ATOGpuPipeline.PassUnpremultiplyEncodeSRGB, tw, th);
                            chain.Add(bytesRt);
                        }
                        else
                        {
                            bytesRt = _gpu.EncodeToDisplay(finalRt, ATOGpuPipeline.PassLinearCopy, tw, th);
                            chain.Add(bytesRt);
                        }

                        var pixels = _gpu.ReadbackRegion32(bytesRt, new RectInt(0, 0, tw, th));
                        var readable = new Texture2D(tw, th, TextureFormat.RGBA32, false, false);
                        byte[] png;
                        bool hasAlpha = false;
                        bool isGray = true;
                        try
                        {
                            readable.SetPixels32(pixels, 0);
                            readable.Apply(false, false);
                            foreach (var p in pixels)
                            {
                                if (p.a < 250) hasAlpha = true;
                                if (isGray && (Mathf.Abs(p.g - p.r) > 2 || Mathf.Abs(p.b - p.r) > 2)) isGray = false;
                                if (hasAlpha && !isGray) break;
                            }
                            png = ImageConversion.EncodeToPNG(readable);
                        }
                        finally
                        {
                            Object.DestroyImmediate(readable);
                        }

                            var role = DominantRoleOf(tex);
                        var layer = new ATOGeneratedLayer
                        {
                            role = role,
                            width = tw,
                            height = th,
                            pngBytes = png,
                            sRGB = tex.sRGB,
                            hasAlpha = hasAlpha,
                            isNormal = normalPath,
                            isEffectivelyGray = isGray,
                        };
                        var category = ATOCategoryClassifier.ForLayer(role, hasAlpha);
                        string name = ATOConsts.ScaledPrefix + ShortHash(tex.contentHash ?? tex.texture.name);
                        // Standalone textures keep original wrap (UVs unchanged).
                        // 整图缩放保留源 wrap（UV 不变，平铺依赖 wrap）。
                        var written = _writer.Write(name, layer, category, tex.filterMode, out _,
                            tex.wrapModeU, tex.wrapModeV);
                        written.layerScale = ratio;
                        _model.report.standaloneTextures.Add(new ATOBuildReport.TextureInfo
                        {
                            name = tex.texture.name,
                            fromWidth = tex.width, fromHeight = tex.height,
                            toWidth = tw, toHeight = th,
                            sourceBytes = tex.sourceBytes,
                            resultBytes = written.bytes,
                            reason = reasonTag,
                        });
                        _model.report.optimizedTextureBytes += written.bytes;
                        return written;
                    }
                    finally
                    {
                        foreach (var rt in chain) _gpu.Pool.Return(rt);
                    }
                }
                catch (ATOCancelledException) { throw; }
                catch (Exception e)
                {
                    ATOLog.Warn($"standalone scale failed for {(tex.texture != null ? tex.texture.name : "?")}: {e.Message}");
                    _model.report.warnings.Add(ATOLoc.T("ato:standalone.failed",
                        tex.texture != null ? tex.texture.name : "?", e.Message));
                    return null;
                }
                finally
                {
                    session?.Dispose();
                }
            }
        }

        private readonly Dictionary<ATOTextureEntry, float> _wholeRatioCache = new Dictionary<ATOTextureEntry, float>();

        private float RequiredWholeTextureRatio(ATOTextureEntry tex)
        {
            if (_wholeRatioCache.TryGetValue(tex, out var cached)) return cached;
            float req = 1f;
            // The evaluator stores per-island ratios; the strictest island that
            // references this texture wins (whole texture must satisfy its worst user).
            // 评估器存逐岛比例；引用该贴图的最严格岛生效（整图须满足最差用户）。
            foreach (var g in _model.uvGroups)
            {
                if (!g.OptimizableTextures().Contains(tex)) continue;
                if (_lastQuality == null || !_lastQuality.TryGetValue(g, out var m) || m == null) continue;
                foreach (var kv in m)
                {
                    req = Mathf.Max(req, Mathf.Max(kv.Value.x, kv.Value.y));
                }
            }
            req = Mathf.Clamp01(req);
            _wholeRatioCache[tex] = req;
            return req;
        }

        private ATORole DominantRoleOf(ATOTextureEntry tex)
        {
            foreach (var g in _model.uvGroups)
            {
                foreach (var u in g.usages)
                    if (u.texture == tex && u.Optimizable) return u.role;
            }
            return ATORole.Main;
        }

        // ==================================================================
        // Finish / 收尾
        // ==================================================================

        private void FinishReport()
        {
            // Every UV group must end with a visible disposition (report contract).
            // 每个 UV 组的最终处置必须可见（报告契约）。
            foreach (var g in _model.uvGroups)
            {
                if (string.IsNullOrEmpty(g.FinalDisposition))
                    g.FinalDisposition = g.OptimizableTextures().Any()
                        ? "kept original (quality=1 or threshold unreachable) / 保持原样（质量=1 或阈值不可达）"
                        : "whitelist: no optimizable texture / 无可优化贴图";
                _model.report.groupDispositions.Add($"{ContextOf(g)} -> {g.FinalDisposition}");
            }
            // Extension hook: handlers may append notes before submission.
            // 扩展钩子：提交前可追加备注。
            ATOExtensionApi.NotifyBeforeReport(_model.report);
            _model.report.Submit();
            ATOLog.Info(_model.report.BuildSummaryText());
        }
    }
}
