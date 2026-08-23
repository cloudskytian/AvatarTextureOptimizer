using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.API;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Fosa.AvatarTextureOptimizer.Editor.Inspector;
using Fosa.AvatarTextureOptimizer.Editor.Quality;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using VRC.SDK3.Avatars.Components;
using Debug = UnityEngine.Debug;

namespace Fosa.AvatarTextureOptimizer.Editor.Pipeline
{
    internal static class ATOPipeline
    {
        public static void Run(BuildContext context)
        {
            var components = context.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (components.Length == 0) return;
            if (components.Length != 1 || components[0].gameObject != context.AvatarRootObject ||
                context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>() == null)
            {
                var language = components.Length == 0 ? ATOLanguage.Auto : components[0].language;
                ErrorReport.ReportError(new ATOReportError(ErrorSeverity.Error,
                    ATOI18n.Get(language, components.Length != 1 ? "error.multiple" : "error.root")));
                return;
            }

            var component = components[0]; var total = Stopwatch.StartNew();
            AtlasBuildResult transientAtlases = null;
            WholeTextureOptimizer.Result transientWhole = null;
            Dictionary<Renderer, Mesh> transientMeshes = null;
            AAOUvCompatibilityBridge aaoBridge = null;
            IATOCommitTransaction deferredCommit = null;
            var cleanupTransientResources = true;
            ATOProgress.Begin();
            try
            {
                var activeBuildTarget = EditorUserBuildSettings.activeBuildTarget;
                var settings = component.Resolve(CurrentPlatform());
                if (!IsSupportedBuildTarget(activeBuildTarget))
                {
                    var message = string.Format(ATOI18n.Get(component.language, "info.platformBypass"),
                        activeBuildTarget);
                    Debug.LogWarning("[ATO] " + message);
                    ErrorReport.ReportError(new ATOReportError(ErrorSeverity.Information, message));
                    CompleteSuccessfulRun(component, null);
                    return;
                }
                var extensions = ATOExtensionRegistry.Snapshot();
                var preAnalysisWarnings = new List<string>();
                RunBeforeAnalysisExtensions(context.AvatarRootObject, component, settings, extensions,
                    preAnalysisWarnings);
                if (!SupportsRequiredGpuCapabilities(SystemInfo.supportsComputeShaders,
                        SystemInfo.supportsAsyncGPUReadback, SystemInfo.IsFormatSupported))
                {
                    var message = ATOI18n.Get(component.language, "info.gpuBypass");
                    Debug.LogWarning("[ATO] " + message);
                    ErrorReport.ReportError(new ATOReportError(ErrorSeverity.Information, message));
                    CompleteSuccessfulRun(component, null);
                    return;
                }
                // Quality 1 is a strict no-resampling contract. The current atlas and whole-texture paths both alter
                // sampling (type conversion, padding and mip generation), even when the mip-0 dimensions are equal.
                // Preserve the original resources until a separately proven texel-copy path exists.
                if (RequiresStrictQualityBypass(settings))
                {
                    var message = ATOI18n.Get(component.language, "info.qualityOneBypass");
                    Debug.Log("[ATO] " + message);
                    ErrorReport.ReportError(new ATOReportError(ErrorSeverity.Information, message));
                    CompleteSuccessfulRun(component, null);
                    return;
                }
                ATOProgress.Show("Analysis", "Scanning renderers, materials, animation, and texture fingerprints", 0.03f);
                var stage = Stopwatch.StartNew();
                var analysis = new AvatarAnalyzer(context, component, settings, extensions, preAnalysisWarnings).Analyze();
                aaoBridge = new AAOUvCompatibilityBridge();
                if (settings.generateAtlases) aaoBridge.Analyze(analysis);
                LogStage(component, "Analysis", stage, "renderers=" + analysis.Renderers.Count + ", bindings=" + analysis.TextureBindings.Count());
                if (component.debug != null && component.debug.analysis)
                    foreach (var binding in analysis.TextureBindings)
                        Debug.Log("[ATO] Source: renderer=" + binding.Renderer.Path + ", slot=" + binding.Slot.Slot +
                                  ", property=" + binding.PropertyName + ", texture=" + binding.OriginalTexture.name +
                                  ", canonical=" + binding.Texture.name + ", kind=" + binding.Kind + ", uv=" + binding.UvChannel +
                                  ", safe=" + binding.AtlasSafe);

                ATOProgress.Show("UV islands", "Extracting connected and overlapping triangle coverage", 0.15f);
                stage.Restart(); UvAnalysisStage.Execute(analysis, settings.generateAtlases, settings);
                if (settings.generateAtlases) UvAnalysisStage.EnforceAnimatedTextureIdentityClosure(analysis);
                LogStage(component, "UV", stage, "islands=" + analysis.UvGroups.Sum(value => value.Islands.Count));
                if (component.debug != null && component.debug.uvIslands)
                    foreach (var group in analysis.UvGroups) foreach (var island in group.Islands)
                        Debug.Log("[ATO] Island: group=" + group.Id + ", island=" + island.Id + ", uv=" + island.UvBounds +
                                  ", sourcePixels=" + island.OriginalPixelBounds + ", areaM2=" + island.SurfaceAreaSquareMeters);

                ATOProgress.Show("Quality", "Solving quality and pixel-density bounds", 0.28f);
                stage.Restart(); new IslandSizeSolver().Solve(analysis, settings);
                LogStage(component, "Quality", stage, "safeGroups=" + analysis.UvGroups.Count(value => value.AtlasSafe));
                if (component.debug != null && component.debug.quality)
                    foreach (var group in analysis.UvGroups.Where(value => value.AtlasSafe)) foreach (var island in group.Islands)
                        Debug.Log("[ATO] Quality result: group=" + group.Id + ", island=" + island.Id + ", target=" +
                                  island.TargetPixelSize + ", scale=" + island.Scale + ", pure=" + island.PureColor);

                if (!settings.generateAtlases)
                {
                    RunBeforeCommit(context, component, settings, analysis, extensions);
                    ATOProgress.Show("Whole textures", "Resampling complete textures without changing meshes or UVs", 0.72f);
                    stage.Restart(); WholeTextureOptimizer.Result whole;
                    WholeTextureOptimizer optimizer = null;
                    try
                    {
                        optimizer = new WholeTextureOptimizer(settings, context.AssetSaver,
                            context.Extension<AnimatorServicesContext>().AnimationIndex);
                        LogLifetime(component, "Whole-texture GPU resampler allocated");
                        whole = optimizer.BuildAndCommit(analysis);
                        transientWhole = whole;
                        deferredCommit = whole.CommitTransaction;
                    }
                    finally
                    {
                        if (optimizer != null)
                        {
                            optimizer.Dispose(); LogLifetime(component, "Whole-texture GPU resampler disposed");
                        }
                    }
                    LogStage(component, "Whole textures", stage, "textures=" + whole.Replacements.Values.Distinct().Count() +
                        ", pixels=" + whole.OutputPixels);
                    ReportFallbacks(analysis, component.language);
                    ReportSummary(component.language, false,
                        analysis.UvGroups.Sum(value => value.Islands.Count),
                        whole.Replacements.Values.Where(value => value != null).Distinct().Count(),
                        EstimateWholeTextureAreaSaving(analysis, whole), total.ElapsedMilliseconds);
                    CompleteSuccessfulRun(component, deferredCommit);
                    // The Avatar/IAssetSaver owns even non-persistent outputs after the transaction completes.
                    // transaction 完成后，即使输出仍是 transient，也已由 Avatar/IAssetSaver 接管。
                    cleanupTransientResources = false;
                    return;
                }

                ATOProgress.Show("Packing", "BLF shape packing into atlas candidate pools", 0.55f);
                stage.Restart(); var plan = new ShapeAtlasPacker().Build(analysis, settings);
                LogStage(component, "Packing", stage, "pages=" + plan.Pages.Count);
                if (component.debug != null && component.debug.packing)
                    foreach (var page in plan.Pages)
                    {
                        var content = page.Placements.Sum(value => (long)value.ContentRect.width * value.ContentRect.height);
                        var utilization = 100.0 * content / Math.Max(1L, (long)page.Size.x * page.Size.y);
                        Debug.Log("[ATO] Page: id=" + page.Id + ", size=" + page.Size + ", groups=" + page.Groups.Count +
                                  ", islands=" + page.Placements.Count + ", contentUtilization=" + utilization.ToString("F1") + "%");
                    }
                if (plan.Pages.Count == 0)
                {
                    ReportFallbacks(analysis, component.language);
                    ReportSummary(component.language, true,
                        analysis.UvGroups.Sum(value => value.Islands.Count), 0, 0.0, total.ElapsedMilliseconds);
                    CompleteSuccessfulRun(component, null); return;
                }

                ATOProgress.Show("Atlas generation", "GPU resampling and pull-push padding", 0.68f);
                stage.Restart(); AtlasBuildResult atlases; AtlasTextureGenerator generator = null;
                try
                {
                    generator = new AtlasTextureGenerator(settings, context.AssetSaver);
                    LogLifetime(component, "Atlas GPU generator allocated");
                    atlases = generator.Generate(analysis, plan);
                    transientAtlases = atlases;
                }
                finally
                {
                    if (generator != null)
                    {
                        generator.Dispose(); LogLifetime(component, "Atlas GPU generator disposed");
                    }
                }
                LogStage(component, "Atlases", stage, "textures=" + atlases.AllTextures.Distinct().Count() +
                    ", pixels=" + atlases.OutputPixels);
                if (plan.Pages.Count == 0)
                {
                    ReportFallbacks(analysis, component.language);
                    Debug.Log("[ATO] All atlas pages retained original meshes and textures after final quality verification.");
                    ReportSummary(component.language, true,
                        analysis.UvGroups.Sum(value => value.Islands.Count), 0, 0.0, total.ElapsedMilliseconds);
                    CompleteSuccessfulRun(component, null);
                    return;
                }
                if (component.debug != null && component.debug.generatedAssets)
                    foreach (var texture in atlases.AllTextures.Distinct())
                        Debug.Log("[ATO] Generated texture: " + texture.name + ", " + texture.width + "x" + texture.height +
                                  ", format=" + texture.format + ", mips=" + texture.mipmapCount);

                ATOProgress.Show("Mesh remapping", "Splitting vertices and preserving all streams", 0.88f);
                stage.Restart(); var meshes = new MeshAtlasRemapper(aaoBridge).Build(analysis, plan);
                transientMeshes = meshes;
                RunBeforeCommit(context, component, settings, analysis, extensions);

                ATOProgress.Show("Commit", "Rewriting materials, texture animation, and mesh references", 0.96f);
                if (component.debug != null && component.debug.animationRewrite)
                {
                    var index = context.Extension<AnimatorServicesContext>().AnimationIndex;
                    Debug.Log("[ATO] Animation rewrite candidates: clips=" + analysis.Renderers.SelectMany(value => index.GetClipsForObjectPath(value.Path)).Distinct().Count() +
                              ", textureBindings=" + analysis.TextureBindings.Count(value => value.IsAnimatedValue));
                }
                // Renderer/curve changes are applied first but stay reversible. AAO registration and every reporting
                // or component-removal step are inside the same deferred completion boundary.
                // 先应用但不完成提交；AAO、最终报告和组件移除全部成功后才正式完成。
                deferredCommit = new MaterialAnimationRewriter(context.AssetSaver,
                    context.Extension<AnimatorServicesContext>().AnimationIndex, settings.deduplicateMaterials,
                    settings.mergeSafeOpaqueMaterialSlots)
                    .Apply(analysis, plan, atlases, meshes);
                aaoBridge.Register();
                LogStage(component, "Commit", stage, "meshes=" + meshes.Count);
                ReportFallbacks(analysis, component.language);
                ReportSummary(component.language, true,
                    analysis.UvGroups.Sum(value => value.Islands.Count),
                    atlases.AllTextures.Distinct().Count(),
                    EstimateAtlasTextureAreaSaving(analysis, plan, atlases), total.ElapsedMilliseconds);
                CompleteSuccessfulRun(component, deferredCommit);
                // The committed Avatar and IAssetSaver now own these resources. SaveAsset is allowed to be a no-op
                // (NDMF NullAssetSaver), so ownership transfer must not be inferred from EditorUtility.IsPersistent.
                // 成功后资源由 Avatar/IAssetSaver 接管，不能以是否 persistent 推断是否可销毁。
                cleanupTransientResources = false;
            }
            catch (OperationCanceledException exception)
            {
                cleanupTransientResources = RollbackDeferredCommit(aaoBridge, deferredCommit);
                ErrorReport.ReportError(new ATOReportError(ErrorSeverity.NonFatal,
                    ATOI18n.Get(component.language, "error.cancelled") + " " + exception.Message));
                throw;
            }
            catch (Exception exception)
            {
                var rollbackRestored = RollbackDeferredCommit(aaoBridge, deferredCommit);
                // Apply can fail before its transaction is returned. Its dedicated exception carries the otherwise
                // unavailable information that generated assets may still be referenced by the Avatar.
                cleanupTransientResources = CanCleanupAfterRollback(exception, rollbackRestored);
                ErrorReport.ReportException(exception,
                    "[ATO] " + string.Format(ATOI18n.Get(component.language, "error.internal"), exception.Message));
                throw;
            }
            finally
            {
                // Nothing in final cleanup may escape after the build-only marker has been removed. Successful
                // resources have explicit transferred ownership even when non-persistent; failure cleanup runs only
                // after complete restoration, and each eligible release is attempted independently.
                // 删除构建标记后 finally 不得抛异常；成功资源显式转移所有权，失败资源仅在完整恢复后独立释放。
                try { deferredCommit?.Dispose(); }
                catch (Exception exception) { Debug.LogError("[ATO] Deferred transaction disposal failed: " + exception); }
                try { DestroyTransientWholeIfOwned(cleanupTransientResources, transientWhole); }
                catch (Exception exception) { Debug.LogError("[ATO] Transient whole-texture cleanup failed: " + exception); }
                try { DestroyTransientAtlasesIfOwned(cleanupTransientResources, transientAtlases); }
                catch (Exception exception) { Debug.LogError("[ATO] Transient atlas cleanup failed: " + exception); }
                try { DestroyTransientMeshesIfOwned(cleanupTransientResources, transientMeshes); }
                catch (Exception exception) { Debug.LogError("[ATO] Transient mesh cleanup failed: " + exception); }
                try { ATOProgress.Clear(); }
                catch (Exception exception) { Debug.LogError("[ATO] Progress cleanup failed: " + exception); }
            }
        }

        internal static bool CanCleanupAfterRollback(Exception pipelineException, bool rollbackRestored)
        {
            return rollbackRestored && !(pipelineException is ATORollbackIncompleteException);
        }

        internal static void DestroyTransientWholeIfOwned(bool cleanupTransientResources,
            WholeTextureOptimizer.Result transientWhole)
        {
            if (cleanupTransientResources && transientWhole != null)
                WholeTextureOptimizer.DestroyTransientTextures(transientWhole.GeneratedTextures);
        }

        internal static void DestroyTransientAtlasesIfOwned(bool cleanupTransientResources,
            AtlasBuildResult transientAtlases)
        {
            if (cleanupTransientResources && transientAtlases != null) transientAtlases.DestroyTransient();
        }

        internal static void DestroyTransientMeshesIfOwned(bool cleanupTransientResources,
            IReadOnlyDictionary<Renderer, Mesh> transientMeshes)
        {
            if (cleanupTransientResources && transientMeshes != null)
                MeshAtlasRemapper.DestroyTransient(transientMeshes.Values);
        }

        internal static void CompleteSuccessfulRun(AvatarTextureOptimizer component,
            IATOCommitTransaction commitTransaction)
        {
            // Every successful or intentional bypass path removes the build-only marker. Complete is deliberately
            // last and is contractually non-throwing, so no fallible pipeline work occurs after rollback is disabled.
            // 所有成功/主动跳过路径都移除构建标记；Complete 必须不抛异常，并且始终是最后一步。
            if (component != null) UnityEngine.Object.DestroyImmediate(component);
            commitTransaction?.Complete();
        }

        internal static bool RollbackDeferredCommit(AAOUvCompatibilityBridge aaoBridge,
            IATOCommitTransaction commitTransaction)
        {
            // Reverse commit order: AAO registration happened after renderer/curve mutation. Never let one rollback
            // failure prevent the other recovery attempt or hide the original pipeline exception. External generated
            // resources may be released only when every rollback participant confirms complete restoration.
            var restored = true;
            try
            {
                if (aaoBridge != null) restored &= aaoBridge.Rollback();
            }
            catch (Exception exception)
            {
                restored = false;
                Debug.LogError("[ATO] AAO rollback failed: " + exception);
            }
            try
            {
                if (commitTransaction != null) restored &= commitTransaction.Rollback();
            }
            catch (Exception exception)
            {
                restored = false;
                Debug.LogError("[ATO] Avatar rewrite rollback failed: " + exception);
            }
            return restored;
        }

        private static void RunBeforeCommit(BuildContext context, AvatarTextureOptimizer component,
            ATOOptimizationSettings settings, AvatarAnalysis analysis, IATOExtension[] extensions)
        {
            var warnings = new List<string>();
            RunBeforeCommitExtensions(context.AvatarRootObject, component, settings, extensions, warnings);
            foreach (var warning in warnings.Where(value => !string.IsNullOrWhiteSpace(value)))
                analysis.Fallbacks.Add(new FallbackRecord(component, warning));
        }

        internal static void RunBeforeAnalysisExtensions(GameObject avatarRoot, AvatarTextureOptimizer component,
            ATOOptimizationSettings settings, IEnumerable<IATOExtension> extensions, IList<string> warnings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var extensionContext = new ATOExtensionContext
            {
                AvatarRoot = avatarRoot, Component = component, Settings = settings,
                Warnings = warnings ?? new List<string>()
            };
            foreach (var extension in extensions ?? Enumerable.Empty<IATOExtension>())
                if (extension != null) extension.BeforeAnalysis(extensionContext);
            // Extensions may tune settings only before analysis. Re-establish every serialized-value invariant
            // before capability checks, allocations, or quality decisions.
            AvatarTextureOptimizer.SanitizeSettings(settings);
        }

        internal static void RunBeforeCommitExtensions(GameObject avatarRoot, AvatarTextureOptimizer component,
            ATOOptimizationSettings settings, IEnumerable<IATOExtension> extensions, IList<string> warnings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            // BeforeCommit is observational: changing layout/mip/quality settings after analysis would invalidate its
            // safety proof. Supply a detached snapshot so accidental edits cannot affect the actual commit.
            // BeforeCommit 仅用于观察；提供独立快照，避免分析后改设置破坏安全证明。
            var extensionContext = new ATOExtensionContext
            {
                AvatarRoot = avatarRoot, Component = component, Settings = settings.DeepClone(),
                Warnings = warnings ?? new List<string>()
            };
            foreach (var extension in extensions ?? Enumerable.Empty<IATOExtension>())
                if (extension != null) extension.BeforeCommit(extensionContext);
        }

        private static void LogLifetime(AvatarTextureOptimizer component, string detail)
        {
            if (component != null && component.debug != null && component.debug.resourceLifetime)
                Debug.Log("[ATO] Resource lifetime: " + detail);
        }

        internal static double EstimateWholeTextureAreaSaving(AvatarAnalysis analysis,
            WholeTextureOptimizer.Result result)
        {
            if (analysis == null) return 0.0;
            var bindings = analysis.TextureBindings.Where(value => value != null).ToArray();
            var replacements = result == null
                ? new Dictionary<TextureBindingRecord, Texture2D>()
                : result.Replacements;
            var replaced = new HashSet<TextureBindingRecord>(replacements
                .Where(pair => pair.Key != null && pair.Value != null).Select(pair => pair.Key));
            var outputs = replacements.Values.Where(value => value != null)
                .Concat(bindings.Where(value => !replaced.Contains(value)).Select(SourceTexture));
            return EstimateTextureAreaSaving(bindings.Select(SourceTexture), outputs);
        }

        internal static double EstimateAtlasTextureAreaSaving(AvatarAnalysis analysis, AtlasPlan plan,
            AtlasBuildResult result)
        {
            if (analysis == null) return 0.0;
            var bindings = analysis.TextureBindings.Where(value => value != null).ToArray();
            var replaced = new HashSet<TextureBindingRecord>();
            if (plan != null)
            foreach (var page in plan.Pages.Where(value => value != null))
            foreach (var group in page.Groups.Where(value => value != null))
            foreach (var binding in group.Bindings.Where(value => value != null))
                replaced.Add(binding);
            var generated = result == null ? Enumerable.Empty<Texture2D>() : result.AllTextures;
            var outputs = generated.Concat(bindings.Where(value => !replaced.Contains(value)).Select(SourceTexture));
            return EstimateTextureAreaSaving(bindings.Select(SourceTexture), outputs);
        }

        internal static double EstimateTextureAreaSaving(IEnumerable<Texture2D> inputs,
            IEnumerable<Texture2D> outputs)
        {
            var inputPixels = TextureArea(inputs);
            if (!(inputPixels > 0.0) || double.IsNaN(inputPixels) || double.IsInfinity(inputPixels)) return 0.0;
            var outputPixels = TextureArea(outputs);
            if (double.IsNaN(outputPixels) || double.IsInfinity(outputPixels)) return 0.0;
            var saving = (1.0 - outputPixels / inputPixels) * 100.0;
            return double.IsNaN(saving) || double.IsInfinity(saving) ? 0.0 : saving;
        }

        internal static string FormatSummary(ATOLanguage language, bool atlasMode, int islandCount,
            int textureCount, double estimatedSaving, long elapsedMilliseconds)
        {
            var key = atlasMode ? "report.summary" : "report.summaryNoAtlas";
            return string.Format(CultureInfo.InvariantCulture, ATOI18n.Get(language, key),
                Math.Max(0, islandCount), Math.Max(0, textureCount),
                estimatedSaving.ToString("F1", CultureInfo.InvariantCulture), Math.Max(0L, elapsedMilliseconds));
        }

        private static Texture2D SourceTexture(TextureBindingRecord binding)
        {
            return binding == null ? null : binding.OriginalTexture != null ? binding.OriginalTexture : binding.Texture;
        }

        private static double TextureArea(IEnumerable<Texture2D> textures)
        {
            return (textures ?? Enumerable.Empty<Texture2D>()).Where(value => value != null).Distinct()
                .Sum(value => (double)value.width * value.height);
        }

        private static void ReportSummary(ATOLanguage language, bool atlasMode, int islandCount,
            int textureCount, double estimatedSaving, long elapsedMilliseconds)
        {
            var message = FormatSummary(language, atlasMode, islandCount, textureCount, estimatedSaving,
                elapsedMilliseconds);
            // Report while the deferred transaction is still rollback-capable; an NDMF/UI failure must not leave
            // a partially committed Avatar. / 在事务仍可回滚时写入 NDMF，报告异常不得留下部分提交。
            ErrorReport.ReportError(new ATOReportError(ErrorSeverity.Information, message));
            Debug.Log("[ATO] " + message);
        }

        private static void ReportFallbacks(AvatarAnalysis analysis, ATOLanguage language)
        {
            foreach (var fallback in analysis.Fallbacks)
                ErrorReport.ReportError(new ATOReportError(ErrorSeverity.Information,
                    string.Format(ATOI18n.Get(language, "warning.fallback"),
                        fallback.Subject == null ? "object" : fallback.Subject.name, fallback.Reason)));
        }

        private static void LogStage(AvatarTextureOptimizer component, string stage, Stopwatch watch, string detail)
        {
            if (component != null && component.verboseLogging)
                Debug.Log("[ATO] " + stage + ": " + detail + ", ms=" + watch.ElapsedMilliseconds);
        }

        internal static bool RequiresStrictQualityBypass(ATOOptimizationSettings settings)
        {
            return settings == null || settings.EffectiveQuality == null || settings.EffectiveQuality.IsLosslessBypass;
        }

        internal static bool SupportsRequiredGpuCapabilities(bool supportsComputeShaders,
            bool supportsAsyncGpuReadback, Func<GraphicsFormat, FormatUsage, bool> isFormatSupported)
        {
            if (!supportsComputeShaders || !supportsAsyncGpuReadback || isFormatSupported == null) return false;
            try
            {
                return SupportsWorkFormat(GraphicsFormat.R16G16B16A16_SFloat, isFormatSupported) &&
                       SupportsWorkFormat(GraphicsFormat.R8_UNorm, isFormatSupported);
            }
            catch
            {
                // A graphics-backend capability query is part of the safety proof. Query failure must preserve the
                // original Avatar rather than falling through to allocations. / 能力查询失败时整次保守旁路。
                return false;
            }
        }

        private static bool SupportsWorkFormat(GraphicsFormat format,
            Func<GraphicsFormat, FormatUsage, bool> isFormatSupported)
        {
            return isFormatSupported(format, FormatUsage.Sample) &&
                   isFormatSupported(format, FormatUsage.Render) &&
                   isFormatSupported(format, FormatUsage.LoadStore);
        }

        internal static bool IsSupportedBuildTarget(BuildTarget target)
        {
            return target == BuildTarget.Android || target == BuildTarget.iOS ||
                   BuildPipeline.GetBuildTargetGroup(target) == BuildTargetGroup.Standalone;
        }

        private static ATOPlatform CurrentPlatform()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android) return ATOPlatform.Android;
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS) return ATOPlatform.IOS;
            return ATOPlatform.PC;
        }
    }
}
