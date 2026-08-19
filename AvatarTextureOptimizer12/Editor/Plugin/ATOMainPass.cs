// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Main NDMF pass (orchestration).
// AvatarTextureOptimizer (ATO) - NDMF 主 Pass（流程编排）。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Net.Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Net.Fosa.AvatarTextureOptimizer.Editor.Apply;
using Net.Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.MeshOps;
using Net.Fosa.AvatarTextureOptimizer.Editor.Quality;
using UnityEditor;
using UnityEngine;
#if ATO_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Plugin
{
    /// <summary>
    /// EN: The single pass that performs all of ATO's work. Keeping everything in one pass lets us hold the
    ///     large native caches for exactly as long as we need them and release them deterministically.
    /// ZH: 承担 ATO 全部工作的唯一 Pass。集中在一个 Pass 内可以精确控制大块原生缓存的生命周期并确定性释放。
    /// </summary>
    public sealed class ATOMainPass : Pass<ATOMainPass>
    {
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer.main";
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Execute(BuildContext ctx)
        {
            var components = ctx.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (components.Length == 0) return;

            if (components.Length > 1)
            {
                ATOReportUtil.Fatal("ATO:error:multiple_components", components[0], components.Length);
                throw new Exception("[ATO] More than one AvatarTextureOptimizer component on this avatar");
            }

            var component = components[0];
#if ATO_VRCSDK3_AVATARS
            if (component.GetComponent<VRCAvatarDescriptor>() == null)
            {
                ATOReportUtil.Fatal("ATO:error:no_descriptor", component);
                throw new Exception("[ATO] AvatarTextureOptimizer must be attached to a VRCAvatarDescriptor");
            }
#endif

            var settings = component.settings;
            ATOLog.BeginBuild(settings.verboseLogging, settings.traceIslandMetrics);
            Quality.GpuImageOps.ResetForNewBuild();

            var platform = DetectPlatform();
            var options = settings.Resolve(platform);
            var report = new ATOBuildReport { Platform = platform, Options = options };

            using (var progress = new ATOProgress("Avatar Texture Optimizer"))
            {
                try
                {
                    Run(ctx, settings, options, options.EffectiveQuality(), progress, report);
                }
                catch (ATOCancelledException)
                {
                    ATOLog.Warn("build cancelled by user; releasing resources");
                    ATOReportUtil.Warn("ATO:warn:cancelled");
                    throw;
                }
                finally
                {
                    TextureIntrospection.ReleaseAll();
                    Quality.GpuImageOps.ReleaseAll();
                    ShaderAnalysis.ClearCache();
                    ATOLog.EndBuild();
                }
            }

            report.Emit();
            UnityEngine.Object.DestroyImmediate(component);
        }

        private static ATOPlatform DetectPlatform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return ATOPlatform.Android;
                case BuildTarget.iOS: return ATOPlatform.iOS;
                default: return ATOPlatform.PC;
            }
        }

        private static void Run(BuildContext ctx, ATOSettings settings, ATOPlatformSettings options,
            ATOQualityParams quality, ATOProgress progress, ATOBuildReport report)
        {
            progress.BeginStage("Analysing animations", 0f, 0.05f);
            AnimationFacts facts;
            using (ATOLog.Stage("Scan animations")) facts = AvatarScan.ScanAnimations(ctx);

            progress.BeginStage("Collecting renderers", 0.05f, 0.10f);
            List<RendererEntry> renderers;
            using (ATOLog.Stage("Collect renderers")) renderers = AvatarScan.CollectRenderers(ctx, facts);
            if (renderers.Count == 0)
            {
                ATOLog.Info("no eligible renderers; nothing to do");
                return;
            }

            progress.BeginStage("Building usage graph", 0.10f, 0.20f);
            HashSet<Texture2D> whitelist;
            using (ATOLog.Stage("Expand whitelist"))
                whitelist = AvatarScan.ExpandWhitelist(settings.whitelist, renderers);

            UsageGraph graph;
            using (ATOLog.Stage("Build usage graph"))
                graph = UsageGraphBuilder.Build(renderers, facts, whitelist);

            report.TextureCount = graph.Textures.Count;
            report.ExcludedCount = graph.Textures.Values.Count(t => t.Excluded);

            // ---- Extension hooks: observe the graph and let third parties veto textures ----
            // ---- 扩展钩子：观察关系图，并允许第三方否决某些贴图 ----
            foreach (var hook in API.ATOExtensionRegistry.Hooks)
            {
                try { hook.OnGraphBuilt(ctx, graph); }
                catch (Exception e) { ATOLog.Warn($"hook {hook.GetType().Name}.OnGraphBuilt threw: {e.Message}"); }
            }
            foreach (var usage in graph.Textures.Values)
            {
                foreach (var hook in API.ATOExtensionRegistry.Hooks)
                {
                    try
                    {
                        if (hook.ShouldOptimise(ctx, usage)) continue;
                        usage.Excluded = true;
                        ATOLog.Debug_($"'{usage.Texture.name}' excluded by {hook.GetType().Name}");
                        break;
                    }
                    catch (Exception e)
                    {
                        ATOLog.Warn($"hook {hook.GetType().Name}.ShouldOptimise threw: {e.Message}");
                    }
                }
            }
            report.ExcludedCount = graph.Textures.Values.Count(t => t.Excluded);

            foreach (var usage in graph.Textures.Values)
            {
                if (usage.Excluded && usage.Reject != SlotRejectReason.None)
                    ATOReportUtil.Warn("ATO:warn:texture_excluded", usage.Texture, usage.Reject.ToString());
                report.OriginalBytes += TextureOutput.EstimateBytes(usage.Texture.width, usage.Texture.height,
                    usage.Texture.format, usage.Texture.mipmapCount > 1);
            }

            var islandSets = new Dictionary<UVSlotKey, UVIslandSet>();
            var plansByTexture = new Dictionary<TextureUsage, List<IslandPlan>>();
            var worldAreaByIsland = new Dictionary<UVIsland, float>();
            var replacement = new Dictionary<Texture2D, Texture2D>();

            if (options.generateAtlas)
            {
                progress.BeginStage("Extracting UV islands", 0.20f, 0.35f);
                using (ATOLog.Stage("Extract UV islands"))
                    IslandStage.Build(graph, renderers, islandSets, plansByTexture, worldAreaByIsland, progress);
                report.IslandCount = plansByTexture.Values.SelectMany(v => v)
                    .Select(p => p.Island).Distinct().Count();
            }

            progress.BeginStage("Evaluating target quality", 0.35f, 0.65f);
            using (ATOLog.Stage("Quality search"))
                QualityStage.SolveAll(graph, plansByTexture, worldAreaByIsland, quality, progress);

            var atlasPlans = new List<AtlasPlan>();
            // EN: island -> (any plan of that island, the atlas it landed in). Every parallel layer of a UV
            //     group shares the island's geometry, so any one plan describes the UV remap for all of them.
            // ZH: 岛 -> (该岛的任一计划, 它所在的图集)。UV 组的每个平行层共享岛的几何，
            //     因此任取一份计划即可描述所有层的 UV 重映射。
            var placedIslands = new Dictionary<UVIsland, (IslandPlan plan, AtlasPlan atlas)>();

            if (options.generateAtlas)
            {
                progress.BeginStage("Packing atlases", 0.65f, 0.80f);
                using (ATOLog.Stage("Rasterise + pack"))
                    atlasPlans = PackStage.Pack(graph, plansByTexture, options, progress);

                foreach (var hook in API.ATOExtensionRegistry.Hooks)
                {
                    try { hook.OnAtlasesPlanned(ctx, atlasPlans); }
                    catch (Exception e)
                    {
                        ATOLog.Warn($"hook {hook.GetType().Name}.OnAtlasesPlanned threw: {e.Message}");
                    }
                }

                foreach (var atlas in atlasPlans)
                foreach (var islandPlan in atlas.Islands)
                {
                    if (!placedIslands.ContainsKey(islandPlan.Island))
                        placedIslands[islandPlan.Island] = (islandPlan, atlas);
                }
                report.AtlasCount = atlasPlans.Count;

                progress.BeginStage("Baking atlases", 0.80f, 0.92f);
                using (ATOLog.Stage("Bake atlases"))
                    BakeStage.BakeAll(ctx, atlasPlans, options, replacement, report, progress);
            }

            // EN: Textures that were never atlased still receive whole-texture optimisation.
            // ZH: 未进入图集的贴图仍会接受整图优化。
            progress.BeginStage("Optimising remaining textures", 0.92f, 0.96f);
            using (ATOLog.Stage("Fallback texture optimisation"))
                BakeStage.OptimiseUnatlased(ctx, graph, options, quality, replacement, report, progress);

            progress.BeginStage("Applying results", 0.96f, 1.0f);

            if (options.dedupTextures)
            {
                using (ATOLog.Stage("Deduplicate generated textures"))
                    ApplyStage.DeduplicateTextures(replacement);
            }

            using (ATOLog.Stage("Rewrite meshes"))
                ApplyStage.RewriteMeshes(ctx, islandSets, placedIslands);

            using (ATOLog.Stage("Rewrite materials & animations"))
                ApplyStage.RewriteMaterials(ctx, graph, replacement);

            if (options.dedupMaterials)
            {
                using (ATOLog.Stage("Deduplicate materials"))
                    ApplyStage.DeduplicateMaterials(ctx, facts, true);
            }

            foreach (var set in islandSets.Values) set.Dispose();
        }
    }
}
