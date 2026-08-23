using System;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.API;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Fosa.AvatarTextureOptimizer.Editor.Integration;
using Fosa.AvatarTextureOptimizer.Editor.Processing;
using Fosa.AvatarTextureOptimizer.Editor.Quality;
using Fosa.AvatarTextureOptimizer.Editor.Reporting;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Fosa.AvatarTextureOptimizer.Editor.Core
{
    /// <summary>EN: Transactional build orchestration with safety fallbacks and cooperative cancellation. ZH: 带安全回退与协作取消的事务式构建编排。</summary>
    internal sealed class AvatarTextureOptimizerPass : Pass<AvatarTextureOptimizerPass>
    {
        public override string DisplayName => "Avatar Texture Optimizer: Analyze and Bake";

        protected override void Execute(BuildContext context)
        {
            var components = context.AvatarRootObject.GetComponentsInChildren<Fosa.AvatarTextureOptimizer.AvatarTextureOptimizer>(true);
            if (components.Length == 0) return;
            var report = new AtoBuildReport();
            if (!Validate(context, components, report)) return;
            var component = components[0];
            component.settings.Validate();
            var platform = ResolvePlatform(component.settings.previewPlatform);

            using (var progress = new BuildProgress("Avatar Texture Optimizer"))
            using (var resources = new ResourceScope())
            {
                try
                {
                    BuildPlan plan;
                    AtoExtensionRegistry.Stage(AtoExtensionStage.BeforeAnalysis, context.AvatarRootObject);
                    using (report.Measure("01 Analyze avatar"))
                        plan = AvatarAnalyzer.Analyze(context, component, platform, progress, resources, report);
                    AtoExtensionRegistry.Stage(AtoExtensionStage.AfterAnalysis, context.AvatarRootObject);
                    using (report.Measure("02 Plan AAO compatibility"))
                        AaoUvCompatibility.Plan(plan, report);
                    using (report.Measure("03 Build UV groups"))
                        UvGroupBuilder.Build(plan, progress, report);
                    using (report.Measure("04 Search quality"))
                        IslandQualityScaler.Scale(plan, progress, report);

                    if (plan.Profile.generateAtlases)
                    {
                        AtoExtensionRegistry.Stage(AtoExtensionStage.BeforeAtlas, context.AvatarRootObject);
                        using (report.Measure("05 Shape packing")) ShapeAtlasPacker.Pack(plan, progress, report);
                        using (report.Measure("06 GPU atlas generation")) AtlasGenerator.Generate(context, plan, progress, resources, report);
                        using (report.Measure("07 Mesh UV remap")) MeshUvRemapper.Remap(context, plan, progress, report);
                    }
                    using (report.Measure("08 Whole-texture fallback"))
                        FallbackTextureProcessor.Process(context, plan, progress, resources, report);
                    using (report.Measure("09 Texture deduplication"))
                        GeneratedAssetDeduplicator.DeduplicateTextures(context, plan, progress, resources, report);
                    using (report.Measure("10 Output compression"))
                        TextureOutputProcessor.Apply(plan, progress, report);
                    using (report.Measure("11 Material deduplication"))
                        MaterialDeduplicator.Deduplicate(context, plan, report);
                    using (report.Measure("12 Opaque slot merge"))
                        MaterialSlotMerger.Merge(context, plan, report);

                    AtoExtensionRegistry.Stage(AtoExtensionStage.AfterBake, context.AvatarRootObject);
                    UnityEngine.Object.DestroyImmediate(component);
                    report.PublishSummary();
                }
                catch (OperationCanceledException exception)
                {
                    report.Error("Build cancelled. Scratch CPU/GPU resources were released; generated temporary assets are retained for diagnosis. " + exception.Message, component);
                }
            }
        }

        private static bool Validate(BuildContext context,
            Fosa.AvatarTextureOptimizer.AvatarTextureOptimizer[] components, AtoBuildReport report)
        {
            if (components.Length != 1)
            {
                report.Error($"Exactly one AvatarTextureOptimizer component is required; found {components.Length}.", context.AvatarRootObject);
                return false;
            }
            var component = components[0];
            if (component.gameObject != context.AvatarRootObject || component.GetComponent<VRCAvatarDescriptor>() == null)
            {
                report.Error("AvatarTextureOptimizer must be attached to the avatar root GameObject that contains VRCAvatarDescriptor.", component);
                return false;
            }
            return true;
        }

        private static OptimizerPlatform ResolvePlatform(OptimizerPlatform selected)
        {
            if (selected != OptimizerPlatform.Auto) return selected;
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return OptimizerPlatform.Android;
                case BuildTarget.iOS: return OptimizerPlatform.IOS;
                default: return OptimizerPlatform.PC;
            }
        }
    }
}
