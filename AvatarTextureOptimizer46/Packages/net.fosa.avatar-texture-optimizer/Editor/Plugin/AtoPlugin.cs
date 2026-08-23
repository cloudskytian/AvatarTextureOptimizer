// SPDX-License-Identifier: MIT
// EN: NDMF plugin definition and the single pass that drives the whole optimizer.
// ZH: NDMF 插件定义，以及驱动整个优化器的唯一 Pass。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Net.Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Net.Fosa.AvatarTextureOptimizer.Editor.Apply;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Dedup;
using Net.Fosa.AvatarTextureOptimizer.Editor.Model;
using UnityEditor;
using UnityEngine;

[assembly: ExportsPlugin(typeof(Net.Fosa.AvatarTextureOptimizer.Editor.Plugin.AtoPlugin))]

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Plugin
{
    /// <summary>
    /// EN: The ATO plugin. It runs in the Optimizing phase, which is after Modular Avatar has finished
    ///     its Transforming work, and is explicitly ordered before Avatar Optimizer.
    /// ZH: ATO 插件。它在 Optimizing 阶段运行——此时 Modular Avatar 的 Transforming 工作已完成——
    ///     并显式排在 Avatar Optimizer 之前。
    /// </summary>
    public sealed class AtoPlugin : Plugin<AtoPlugin>
    {
        /// <inheritdoc/>
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer";
        /// <inheritdoc/>
        public override string DisplayName => "Avatar Texture Optimizer";
        /// <inheritdoc/>
        public override Color? ThemeColor => new Color(0.34f, 0.62f, 0.86f);

        /// <inheritdoc/>
        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                {
                    seq.Run(AtoOptimizePass.Instance)
                        .BeforePlugin("com.anatawa12.avatar-optimizer");
                });
        }
    }

    /// <summary>
    /// EN: The single pass. Keeping the whole pipeline in one pass keeps all GPU resources inside one
    ///     try/finally, which is what guarantees no leak on cancellation.
    /// ZH: 唯一的 Pass。把整个管线放在一个 Pass 中，使所有 GPU 资源都处于同一个 try/finally 内，
    ///     这正是取消时不泄漏资源的保证。
    /// </summary>
    public sealed class AtoOptimizePass : Pass<AtoOptimizePass>
    {
        private const string Stage = "Build";

        /// <inheritdoc/>
        public override string DisplayName => "Avatar Texture Optimizer";

        /// <inheritdoc/>
        protected override void Execute(BuildContext context)
        {
            var components = context.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (components.Length == 0) return;

            if (!Validate(context, components)) return;

            var component = components[0];
            var settings = component.settings ?? new AtoSettings();
            var platform = CurrentPlatform();
            var profile = settings.Resolve(platform);

            AtoLog.Begin(settings.verboseLogging, settings.traceLogging);
            AtoLog.Info(Stage, $"platform={platform}, tier={profile.tier}, atlas={profile.generateAtlas}, npot={profile.allowNpot}");

            using var progress = new AtoProgress("Avatar Texture Optimizer");
            var report = new AtoBuildReport();

            try
            {
                progress.BeginStage("Analyzing shaders and animations", 0.00f, 0.10f);
                var shaders = new ShaderAnalysisService();
                var uvCritical = new HashSet<string>(LilToonShaderAnalyzer.UvCriticalPropertyNames());
                var animation = AnimationAnalyzer.Analyze(context, uvCritical);

                var whitelist = new WhitelistResolver();
                whitelist.Resolve(settings.whitelist ?? new List<UnityEngine.Object>());

                progress.BeginStage("Collecting textures", 0.10f, 0.25f);
                var collector = new AtoCollector(context, profile, whitelist, shaders, animation);
                var collection = collector.Collect(progress);
                report.TotalTextures = collection.Textures.Count;
                report.OptimizableTextures = collection.Textures.Values.Count(e => e.IsOptimizable);

                var wholeTextureReplacements = new Dictionary<Texture, Texture>();
                AtlasStageResult atlasResult = new AtlasStageResult();

                if (profile.generateAtlas)
                {
                    progress.BeginStage("Building UV groups", 0.25f, 0.30f);
                    AtoGrouping.Build(collection);
                    report.Groups = collection.Groups.Count;

                    progress.BeginStage("Evaluating quality and packing atlases", 0.30f, 0.80f);
                    var pipeline = new AtoAtlasPipeline(context, profile, platform,
                        path => animation.MaxAnimatedScale.TryGetValue(path, out var s) ? s : Vector3.one);
                    atlasResult = pipeline.Run(collection, progress);
                    report.Atlases.AddRange(atlasResult.Atlases);
                }
                else
                {
                    progress.BeginStage("Scaling textures", 0.30f, 0.80f);
                    var scaler = new WholeTextureScaler(context, profile, platform);
                    scaler.Run(collection, wholeTextureReplacements, progress);
                }

                progress.BeginStage("Applying results", 0.80f, 0.95f);
                var applier = new AtoApplier(context);
                if (profile.generateAtlas)
                    applier.RewriteMeshes(atlasResult, collection.Renderers, progress);

                applier.RewriteMaterials(new AtoCollectionView
                {
                    AllEntries = collection.Textures.Values,
                    Renderers = collection.Renderers,
                    WholeTextureReplacements = wholeTextureReplacements,
                }, atlasResult, progress);

                progress.BeginStage("Deduplicating", 0.95f, 0.99f);
                var animatedSlots = new Dictionary<string, HashSet<int>>();
                foreach (var kv in animation.AnimatedMaterials)
                    animatedSlots[kv.Key] = new HashSet<int>(kv.Value.Keys);

                var finalDedupe = new FinalDeduplicator(context, profile.dedupeTextures, profile.dedupeMaterials);
                if (profile.dedupeTextures || profile.dedupeMaterials)
                    finalDedupe.Run(collection.Renderers, animatedSlots, progress);

                progress.BeginStage("Finalizing", 0.99f, 1.00f);
                report.TexturesDeduplicated = finalDedupe.TexturesRemoved;
                report.MaterialsDeduplicated = finalDedupe.MaterialsRemoved;
                report.SlotsMerged = finalDedupe.SlotsMerged;
                report.Finish(collection, atlasResult);
                report.Publish();
            }
            catch (AtoCancelledException)
            {
                AtoLog.Warning(Stage, "build cancelled; GPU and CPU resources released, temporary assets left on disk.");
                AtoReporting.Warn(Stage, "ATO:warn:cancelled", null);
            }
            catch (Exception e)
            {
                AtoLog.Exception(Stage, e);
                AtoReporting.Fatal(Stage, "ATO:error:internal", null, e.Message);
                throw;
            }
            finally
            {
                // EN: The component must not survive into the finished avatar.
                // ZH: 该组件绝不能残留在成品 Avatar 上。
                foreach (var c in components)
                    if (c != null)
                        UnityEngine.Object.DestroyImmediate(c);

                Resources.UnloadUnusedAssets();
                AtoLog.End();
            }
        }

        /// <summary>
        /// EN: Enforces the "exactly one component, on the avatar descriptor" rule.
        /// ZH: 强制执行“恰好一个组件，且挂在 Avatar Descriptor 上”的规则。
        /// </summary>
        private static bool Validate(BuildContext context, AvatarTextureOptimizer[] components)
        {
            if (components.Length > 1)
            {
                AtoReporting.Fatal(Stage, "ATO:error:multipleComponents", components[0], components.Length.ToString());
                throw new Exception("[ATO] More than one Avatar Texture Optimizer component was found on this avatar.");
            }

            var component = components[0];
#if ATO_VRCSDK3_AVATARS
            if (component.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() == null)
            {
                AtoReporting.Fatal(Stage, "ATO:error:notOnDescriptor", component);
                throw new Exception("[ATO] The Avatar Texture Optimizer component must be placed on the object holding the VRCAvatarDescriptor.");
            }
#endif
            if (component.gameObject != context.AvatarRootObject)
            {
                AtoReporting.Fatal(Stage, "ATO:error:notOnRoot", component);
                throw new Exception("[ATO] The Avatar Texture Optimizer component must be placed on the avatar root.");
            }
            return true;
        }

        /// <summary>
        /// EN: The platform the current build targets.
        /// ZH: 当前构建的目标平台。
        /// </summary>
        public static AtoPlatform CurrentPlatform()
        {
            return EditorUserBuildSettings.activeBuildTarget switch
            {
                BuildTarget.Android => AtoPlatform.Android,
                BuildTarget.iOS => AtoPlatform.iOS,
                _ => AtoPlatform.PC,
            };
        }
    }
}
