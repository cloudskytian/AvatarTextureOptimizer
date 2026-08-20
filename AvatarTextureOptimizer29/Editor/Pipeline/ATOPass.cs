// ATO main pass: validates component, resolves platform settings, runs all stages with
// progress & cancellation, releases resources in finally, removes itself from the product.
// ATO 主 Pass：组件校验、平台设置解析、全阶段执行（进度+取消）、finally 释放资源、
// 从成品移除自身。

using System;
using nadena.dev.ndmf;
using net.fosa.ato;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal class ATOPass : Pass<ATOPass>
    {
        public override string DisplayName => "ATO: Optimize Avatar Textures";

        protected override void Execute(BuildContext ctx)
        {
            ATOLog.ResetTimings();
            ATOLog.Level = AtoLogLevel.Info;

            AtoSession s = null;
            try
            {
                // ---- component validation (spec: exactly one, on descriptor) ----
                var components = ctx.AvatarRootObject
                    .GetComponentsInChildren<AvatarTextureOptimizer>(true);
                if (components.Length == 0) return; // no ATO -> nothing to do / 无组件直接跳过
                if (components.Length > 1)
                {
                    ErrorReport.ReportError(ATOL10n.NdmfLocalizer, ErrorSeverity.Error, "err.placement",
                        $"{components.Length} components found (expected 1)");
                    throw new InvalidOperationException("ATO component placement invalid");
                }

                var comp = components[0];
                if (comp.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() == null)
                {
                    ErrorReport.ReportError(ATOL10n.NdmfLocalizer, ErrorSeverity.Error, "err.placement",
                        $"component on '{comp.gameObject.name}' has no VRCAvatarDescriptor");
                    throw new InvalidOperationException("ATO component placement invalid");
                }

                // ---- session / 会话 ----
                var platform = DetectPlatform();
                var ps = comp.GetPlatformSettings(platform);
                s = new AtoSession
                {
                    ctx = ctx,
                    component = comp,
                    platform = platform,
                    settings = ps.useOverride ? ps : comp.pcSettings, // common best / 通用最优解
                };
                s.quality = QualityPresets.Effective(s);
                s.qualityIsOne = QualityPresets.IsQualityOne(s);
                ATOLog.Level = comp.logLevel;
                ATOLog.Info($"ATO bake start: platform={platform} atlas={comp.generateAtlas} " +
                            $"preset={(s.settings.useOverride ? ps.preset : comp.pcSettings.preset)} " +
                            $"qualityIsOne={s.qualityIsOne} npot={s.settings.experimentalNpot}");

                var stageCtx = new ATOStageContext(ctx, comp, platform, s.warnings);
                ATOExtensionRegistry.Emit(ATOStage.BeforeScan, stageCtx);

                // ---- stages / 阶段 ----
                Progress.Stage("Animation Analysis", 0.02f, 0.08f);
                s.anim = AnimationAnalyzer.Collect(s);

                Progress.Stage("Scan Avatar", 0.10f, 0.08f);
                AvatarScanner.Scan(s);

                Progress.Stage("Build Usage Graph", 0.18f, 0.10f);
                UsageGraph.Build(s);

                Progress.Stage("Extract UV Islands", 0.28f, 0.12f);
                IslandExtractor.Extract(s);
                ATOExtensionRegistry.Emit(ATOStage.AfterAnalysis, stageCtx);

                Progress.Stage("Quality Scaling", 0.40f, 0.20f);
                QualityEvaluator.Evaluate(s);
                ATOExtensionRegistry.Emit(ATOStage.AfterQuality, stageCtx);

                if (s.component.generateAtlas)
                {
                    Progress.Stage("Pack Atlases", 0.60f, 0.08f);
                    BitmaskPacker.Pack(s);
                    ATOExtensionRegistry.Emit(ATOStage.AfterPack, stageCtx);

                    Progress.Stage("Compose Atlases", 0.68f, 0.10f);
                    AtlasBuilder.Build(s);

                    Progress.Stage("Rewrite Meshes", 0.78f, 0.05f);
                    MeshRewriter.Rewrite(s);

                    Progress.Stage("Patch Materials", 0.83f, 0.05f);
                    MaterialPatcher.Patch(s);
                }
                else
                {
                    Progress.Stage("Whole-image Scaling", 0.68f, 0.10f);
                    MaterialPatcher.Patch(s); // registers whole-scale replacements only / 仅登记整图缩放替换
                }

                // dedup BEFORE compression: pixel hashing needs readable RGBA32 pages
                // 去重先于压缩：像素哈希需要可读的 RGBA32 页
                Progress.Stage("Final Dedup", 0.88f, 0.04f);
                FinalDedup.Run(s);

                Progress.Stage("Texture Parameters", 0.92f, 0.04f);
                TextureParams.Apply(s);
                ATOExtensionRegistry.Emit(ATOStage.AfterApply, stageCtx);

                // ---- report & remove self / 报告并移除自身 ----
                ATOExtensionRegistry.Emit(ATOStage.Finish, stageCtx);
                ATOReport.Emit(s);

                foreach (var c in ctx.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true))
                    Object.DestroyImmediate(c);
                ATOLog.Info("ATO bake finished");
            }
            catch (AtoCancelledException)
            {
                ATOLog.Warn("bake cancelled by user");
                ATOReport.EmitCancelled();
                throw; // abort build; temp assets kept by NDMF container / 中止构建，临时资产保留
            }
            finally
            {
                Progress.Clear();
                TexturePixels.DisposeAll();
                QualityEvaluator.DisposeTemp();
                GC.Collect(); // release managed pixel buffers / 释放托管像素缓冲
            }
        }

        private static AtoPlatform DetectPlatform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return AtoPlatform.Android;
                case BuildTarget.iOS: return AtoPlatform.iOS;
                default: return AtoPlatform.PC;
            }
        }
    }
}
