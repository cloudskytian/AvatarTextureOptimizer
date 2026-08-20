using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
#if ATO_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: The single NDMF pass that runs the whole optimisation.
    ///
    ///     Pipeline, in order:
    ///       1. Validate the component (exactly one, on a VRCAvatarDescriptor).
    ///       2. Resolve the platform profile.
    ///       3. Analyse animations, then collect material slots.
    ///       4. Analyse shaders and build the UV group graph, marking unsafe references as whitelisted.
    ///       5. Decode and analyse every candidate texture on the GPU, then deduplicate the inputs.
    ///       6. Build UV islands and solve the per-island scale against the quality profile.
    ///       7. Either pack islands into atlases, or - when atlas generation is off - rescale whole
    ///          textures instead.
    ///       8. Rewrite meshes and material texture references.
    ///       9. Deduplicate the outputs, publish the report, remove ourselves from the avatar.
    ///
    /// ZH: 执行整个优化流程的唯一 NDMF Pass。
    ///
    ///     流水线顺序：
    ///       1. 校验组件（有且仅有一个，且挂在 VRCAvatarDescriptor 上）。
    ///       2. 解析平台配置。
    ///       3. 分析动画，随后收集材质槽。
    ///       4. 分析着色器并构建 UV 组图，把不安全的引用标记为白名单。
    ///       5. 在 GPU 上解码并分析所有候选贴图，随后对输入去重。
    ///       6. 构建 UV 岛，并按质量配置求解每个岛的缩放。
    ///       7. 把岛装箱成图集；若关闭图集生成，则改为对整张贴图缩放。
    ///       8. 重写网格与材质贴图引用。
    ///       9. 对输出去重、发布报告、从 Avatar 上移除自身。
    /// </summary>
    public sealed class ATOPass : Pass<ATOPass>
    {
        /// <inheritdoc/>
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer.main";
        /// <inheritdoc/>
        public override string DisplayName => "Avatar Texture Optimizer";

        /// <inheritdoc/>
        protected override void Execute(BuildContext ctx)
        {
            var components = ctx.AvatarRootTransform
                .GetComponentsInChildren<AvatarTextureOptimizer>(true)
                .ToList();

            if (components.Count == 0) return;

            if (components.Count > 1)
            {
                ErrorReport.ReportError(new MultipleComponentsError(components));
                throw new Exception("[ATO] More than one Avatar Texture Optimizer component on the avatar.");
            }

            var component = components[0];

#if ATO_VRCSDK3_AVATARS
            if (component.GetComponent<VRCAvatarDescriptor>() == null)
            {
                ErrorReport.ReportError(new MissingDescriptorError(component));
                throw new Exception("[ATO] The component must be placed on a GameObject with a VRCAvatarDescriptor.");
            }
#endif

            var log = new ATOLog(component.verboseLogging, component.traceLogging);
            var platform = ResolvePlatform();
            var profile = component.ResolveProfile(platform);
            var report = new ATOReport();

            log.Info($"Starting on '{ctx.AvatarRootObject.name}' " +
                     $"(platform={platform}, quality={profile.qualityTier}, atlas={profile.generateAtlas}, " +
                     $"npot={profile.experimentalNPOT}, padding>={(int)profile.minPadding}px)");

            using var progress = new ATOProgress(interactive: true);
            using var io = new GPUTextureIO(log);

            try
            {
                Run(ctx, component, profile, platform, log, progress, io, report);
            }
            catch (ATOCancelledException)
            {
                log.Warn(ATOLocalizer.Tr("ato.warn.cancelled"));
                ErrorReport.ReportError(new ATOWarning(ATOLocalizer.Tr("ato.warn.cancelled")));
            }
            finally
            {
                // EN: NDMF requires the component to be gone from the built avatar.
                // ZH: NDMF 要求成品 Avatar 上不再残留该组件。
                Object.DestroyImmediate(component);
            }
        }

        /// <summary>
        /// EN: Release the CPU-side pixel copies of every generated texture. Doing this at the very end
        ///     keeps peak memory bounded without breaking the raw-byte comparison used by deduplication.
        /// ZH: 释放所有生成贴图的 CPU 侧像素副本。放在最后执行既能限制峰值内存，
        ///     又不会破坏去重所依赖的原始字节比较。
        /// </summary>
        private static void ReleaseCpuCopies(List<Texture2D> generated, ATOLog log)
        {
            int released = 0;
            foreach (var t in generated)
            {
                if (t == null) continue;
                try { t.Apply(false, true); released++; }
                catch (Exception e) { log.Trace($"Could not release CPU copy of '{t.name}': {e.Message}"); }
            }
            log.Verbose($"Released CPU pixel copies of {released} generated textures");
        }

        private static ATOPlatform ResolvePlatform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return ATOPlatform.Android;
                case BuildTarget.iOS: return ATOPlatform.iOS;
                default: return ATOPlatform.PC;
            }
        }

        private static void Run(BuildContext ctx, AvatarTextureOptimizer component, PlatformProfile profile,
            ATOPlatform platform, ATOLog log, ATOProgress progress, GPUTextureIO io, ATOReport report)
        {
            // ---- 1. Whitelist ---------------------------------------------------------------------
            WhitelistResolver whitelist;
            using (log.Step("Resolve whitelist"))
            {
                progress.Report(ATOLocalizer.Tr("ato.stage.validate"), 0.02f);
                whitelist = new WhitelistResolver(log);
                whitelist.Resolve(component.whitelist);
            }

            // ---- 2. Animations --------------------------------------------------------------------
            AnimationFacts anim;
            using (log.Step("Analyse animations"))
            {
                progress.Report(ATOLocalizer.Tr("ato.stage.animation"), 0.05f);
                anim = AnimationAnalyzer.Analyze(ctx, log);
            }

            // ---- 3. Renderers ---------------------------------------------------------------------
            List<SlotRecord> slots;
            using (log.Step("Collect renderers"))
            {
                progress.Report(ATOLocalizer.Tr("ato.stage.collect"), 0.08f);
                slots = RendererCollector.Collect(ctx, anim, log);
            }
            if (slots.Count == 0) { log.Info("No eligible renderers, nothing to do."); return; }

            // ---- 4. UV groups ---------------------------------------------------------------------
            var builder = new UVGroupBuilder(log, whitelist, anim, ctx.AvatarRootTransform);
            List<UVGroup> groups;
            using (log.Step("Build UV groups"))
            {
                progress.Report(ATOLocalizer.Tr("ato.stage.collect"), 0.12f);
                groups = builder.Build(slots);
            }
            log.Info($"{groups.Count} UV groups, {builder.AllTextures.Count} distinct textures");

            // ---- 5. Texture analysis + input dedup ---------------------------------------------------
            using (log.Step("Analyse textures"))
            {
                progress.Report(ATOLocalizer.Tr("ato.stage.dedupInput"), 0.18f);
                int i = 0;
                foreach (var t in builder.AllTextures.Values)
                {
                    progress.ThrowIfCancelled();
                    io.Analyze(t);
                    report.BytesBefore += TextureOutput.EstimateBytes(t.Source);
                    progress.Report(0.18f + 0.07f * (++i / (float)builder.AllTextures.Count));
                }
                report.InputDuplicatesRemoved = TextureDeduplicator.Deduplicate(builder.AllTextures.Values.ToList(), log);
            }

            // ---- 6. Islands -----------------------------------------------------------------------
            using (log.Step("Build UV islands"))
            {
                progress.Report(ATOLocalizer.Tr("ato.stage.islands"), 0.28f);
                builder.BuildIslands(groups, progress);
            }

            // ---- 7. Quality solve --------------------------------------------------------------------
            var quality = profile.EffectiveQuality;
            using (log.Step("Solve island scales"))
            {
                progress.Report(ATOLocalizer.Tr("ato.stage.quality"), 0.35f);
                var solver = new IslandScaleSolver(io, log, progress);
                int i = 0;
                foreach (var g in groups)
                {
                    if (g.FullyWhitelisted) { i++; continue; }
                    solver.Solve(g, quality, (float)profile.minTexelDensity, (float)profile.maxTexelDensity);
                    progress.Report(0.35f + 0.30f * (++i / (float)groups.Count));
                }
            }

            // ---- 8. Atlas or whole-texture path -------------------------------------------------------
            var remap = new Dictionary<Texture2D, Texture2D>();
            var atlasSizeOf = new Dictionary<UVGroup, Vector2Int>();
            var generated = new List<Texture2D>();

            if (profile.generateAtlas)
            {
                using (log.Step("Pack and composite atlases"))
                {
                    progress.Report(ATOLocalizer.Tr("ato.stage.pack"), 0.66f);
                    AtlasPipeline.Run(ctx, groups, profile, platform, io, log, progress, report,
                        remap, atlasSizeOf, generated);
                }

                // EN: Groups that could not be atlased still deserve whole-texture scaling and the new
                //     import parameters; skipping them entirely would silently lose most of the win on
                //     avatars with one awkward shader.
                // ZH: 无法图集化的组仍应享受整图缩放与新的导入参数；
                //     完全跳过它们会让"只有一个别扭着色器"的 Avatar 静默损失掉大部分收益。
                var fallback = groups.Where(g => g.SkipAtlas && !g.FullyWhitelisted).ToList();
                if (fallback.Count > 0)
                {
                    using (log.Step($"Whole-texture fallback for {fallback.Count} groups"))
                        WholeTexturePipeline.Run(fallback, profile, platform, io, log, progress, report,
                            remap, generated);
                }
            }
            else
            {
                using (log.Step("Rescale whole textures"))
                {
                    progress.Report(ATOLocalizer.Tr("ato.stage.compose"), 0.66f);
                    WholeTexturePipeline.Run(groups, profile, platform, io, log, progress, report, remap, generated);
                }
            }

            // ---- 9. Apply ------------------------------------------------------------------------------
            using (log.Step("Apply meshes and materials"))
            {
                progress.Report(ATOLocalizer.Tr("ato.stage.apply"), 0.85f);

                if (profile.generateAtlas)
                {
                    var rewriter = new MeshRewriter(log);
                    rewriter.Apply(groups, atlasSizeOf);
                }

                var matRewriter = new MaterialRewriter(log);
                matRewriter.Apply(ctx, slots, remap);
            }

            // ---- 10. Output dedup ----------------------------------------------------------------------
            using (log.Step("Deduplicate outputs"))
            {
                progress.Report(ATOLocalizer.Tr("ato.stage.dedupOutput"), 0.93f);
                var renderers = slots.Select(s => s.Renderer).Distinct().ToList();

                if (profile.deduplicateTextures)
                    report.OutputTextureDuplicatesRemoved =
                        PostDeduplicator.DeduplicateTextures(ctx, renderers, generated, log);

                if (profile.deduplicateMaterials)
                    report.MaterialDuplicatesRemoved =
                        PostDeduplicator.DeduplicateMaterials(ctx, renderers, anim, log);
            }

            // EN: Now that dedup has finished comparing raw bytes, drop the CPU-side copies.
            // ZH: 去重已经比较完原始字节，此时可以丢弃 CPU 侧副本。
            ReleaseCpuCopies(generated, log);

            // ---- 11. Report -----------------------------------------------------------------------------
            using (log.Step("Publish report"))
            {
                progress.Report(ATOLocalizer.Tr("ato.stage.report"), 0.98f);
                foreach (var t in generated) report.BytesAfter += TextureOutput.EstimateBytes(t);
                foreach (var g in groups.Where(g => g.SkipAtlas && !string.IsNullOrEmpty(g.SkipReason)))
                    report.Skipped.Add($"{g}: {g.SkipReason}");
                report.Publish(log);
            }
        }
    }
}
