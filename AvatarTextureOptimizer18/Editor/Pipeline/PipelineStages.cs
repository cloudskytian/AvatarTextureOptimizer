using System.Diagnostics;
using UnityEngine;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Apply;
using Fosa.AvatarTextureOptimizer.Editor.Atlases;
using Fosa.AvatarTextureOptimizer.Editor.Islands;
using Fosa.AvatarTextureOptimizer.Editor.Packing;
using Fosa.AvatarTextureOptimizer.Editor.Quality;
using Fosa.AvatarTextureOptimizer.Editor.UvGroups;

namespace Fosa.AvatarTextureOptimizer.Editor.Pipeline
{
    // 管线各阶段（按依赖顺序编排；每阶段：钩子、计时、进度、取消、日志）。Pipeline stages in dependency order.
    internal static class PipelineStages
    {
        // 验证设置。Validates settings.
        public static void Validate(ATOContext ctx)
        {
            RunStage(ctx, "validate", () =>
            {
                ctx.settings.Normalize();
                ATOLog.Debug(string.Format("最小密度 / min density: {0} px/m，最大密度 / max density: {1} px/m，padding: {2} px，最大图集边长 / max atlas side: {3}",
                    ctx.settings.minDensityPxPerMeter, ctx.settings.maxDensityPxPerMeter, ctx.settings.atlasPaddingPx, ctx.settings.ResolveMaxAtlasSize(ctx.platform)));
            });
        }

        // 扫描材质槽。Scans material slots.
        public static void ScanMaterialSlots(ATOContext ctx)
        {
            RunStage(ctx, "scanSlots", () => MaterialSlotScanner.Scan(ctx, CurrentStage(ctx)));
        }

        // 扫描动画。Scans animations.
        public static void ScanAnimations(ATOContext ctx)
        {
            RunStage(ctx, "scanAnimations", () => AnimationScanner.Scan(ctx, CurrentStage(ctx)));
        }

        // 过滤槽位：仅保留“被启用或有动画启用”的渲染器的槽位。Filters slots to enabled-or-animation-enabled renderers.
        public static void FilterSlots(ATOContext ctx)
        {
            RunStage(ctx, "filterSlots", () =>
            {
                var stage = CurrentStage(ctx);
                int removed = 0;
                for (int i = ctx.slots.Count - 1; i >= 0; i--)
                {
                    ctx.CheckCancelled();
                    var slot = ctx.slots[i];
                    slot.rendererToggledByAnimation = ctx.animations.rendererToggled.Contains(slot.renderer);
                    if (!IsEffectivelyEnabled(slot.renderer, ctx))
                    {
                        slot.alwaysDisabled = true;
                        stage.AddLine(string.Format(ATOLocalization.Tr("log.slotSkippedNeverEnabled"), slot.renderer.name, slot.slotIndex));
                        ctx.slots.RemoveAt(i);
                        removed++;
                    }
                }
                ctx.report.slotCount = ctx.slots.Count;
                ctx.report.materialCount = ctx.materials.Count;
                stage.AddLine(string.Format(ATOLocalization.Tr("log.slotCount"), ctx.slots.Count, removed));
            });
        }

        // 收集贴图（含去重）。Collects textures (incl. dedup).
        public static void CollectTextures(ATOContext ctx)
        {
            RunStage(ctx, "collectTextures", () => TextureCollector.Collect(ctx, CurrentStage(ctx)));
        }

        // 白名单解析。Resolves whitelists.
        public static void ResolveWhitelists(ATOContext ctx)
        {
            RunStage(ctx, "whitelist", () => WhitelistResolver.Resolve(ctx, CurrentStage(ctx)));
        }

        // 提取 UV 岛（含越界归一与重叠岛合并）。Extracts UV islands (incl. normalization and overlap merging).
        public static void ExtractIslands(ATOContext ctx)
        {
            RunStage(ctx, "islands", () => IslandExtractor.Extract(ctx, CurrentStage(ctx)));
        }

        // 构建 UV 组与类型组。Builds UV groups and type groups.
        public static void BuildUvGroups(ATOContext ctx)
        {
            RunStage(ctx, "uvgroups", () => UvGroupBuilder.Build(ctx, CurrentStage(ctx)));
        }

        // 目标质量缩放。Quality-gated scaling.
        public static void ScaleIslands(ATOContext ctx)
        {
            RunStage(ctx, "quality", () => IslandScaler.Scale(ctx, CurrentStage(ctx)));
        }

        // 装箱。Packing.
        public static void PackAtlases(ATOContext ctx)
        {
            if (!ctx.settings.generateAtlas) return;
            RunStage(ctx, "packing", () => Packer.Pack(ctx, CurrentStage(ctx)));
        }

        // 生成图集。Builds atlases.
        public static void BuildAtlases(ATOContext ctx)
        {
            if (!ctx.settings.generateAtlas) return;
            RunStage(ctx, "atlases", () => AtlasBuilder.Build(ctx, CurrentStage(ctx)));
        }

        // 应用修改（fallback 贴图 → 网格 → 槽位合并 → 图集去重 → 材质 → 动画重写 → 组件清理）。
        // Apply changes (fallback textures → meshes → slot merge → atlas dedup → materials → animation rewrite → cleanup).
        public static void ApplyChanges(ATOContext ctx)
        {
            RunStage(ctx, "apply", () =>
            {
                var stage = CurrentStage(ctx);
                FallbackTextureProcessor.Process(ctx, stage);
                MeshApplier.Apply(ctx, stage);
                SlotMerger.Merge(ctx, stage);
                TextureDedupPost.Merge(ctx, stage);
                MaterialApplier.Apply(ctx, stage);
                AnimationBindingRemapper.RemapTextureProperties(ctx, stage);
                ComponentCleanup.Clean(ctx);
            });
        }

        // ---- 内部工具 ----

        private static ATOReport.Stage _current;

        private static ATOReport.Stage CurrentStage(ATOContext ctx)
        {
            if (_current == null) _current = ctx.report.BeginStage("?");
            return _current;
        }

        private static void RunStage(ATOContext ctx, string stageId, System.Action body)
        {
            ctx.CheckCancelled();
            Extensions.ATOExtensions.InvokeBefore(stageId, ctx);
            string stageKey = "stage." + stageId;
            var stage = ctx.report.BeginStage(ATOLocalization.Tr(stageKey));
            _current = stage;
            var sw = Stopwatch.StartNew();
            try
            {
                body();
            }
            finally
            {
                sw.Stop();
                ctx.report.EndStage(stage, sw.Elapsed.TotalMilliseconds);
                _current = null;
            }
            Extensions.ATOExtensions.InvokeAfter(stageId, ctx);
        }

        // 渲染器是否“被启用或有动画启用”（含其 GameObject 层级）。
        // Whether a renderer is enabled or may be enabled by animation (including its GameObject hierarchy).
        private static bool IsEffectivelyEnabled(Renderer renderer, ATOContext ctx)
        {
            if (!renderer.enabled && !ctx.animations.rendererToggled.Contains(renderer)) return false;
            var root = ctx.avatarRoot.transform;
            var t = renderer.transform;
            while (t != null)
            {
                if (!t.gameObject.activeSelf)
                {
                    var p = t;
                    bool animatable = false;
                    while (p != null)
                    {
                        if (ctx.animations.objectToggled.Contains(p.gameObject)) { animatable = true; break; }
                        if (p == root) break;
                        p = p.parent;
                    }
                    if (!animatable) return false;
                }
                if (t == root) break;
                t = t.parent;
            }
            return true;
        }
    }
}
