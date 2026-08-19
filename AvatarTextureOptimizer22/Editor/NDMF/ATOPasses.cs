// AvatarTextureOptimizer
// File: Editor/NDMF/ATOPasses.cs
//
// The NDMF pass implementations. Each pass is a separate unit so the pipeline
// can be re-ordered and so per-phase timing/reporting stays clean.
//
// NDMF pass 实现。每个 pass 是独立单元，便于调整流水线顺序，也便于按阶段
// 计时与报告。

using System;
using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.editor.analysis;
using net.fosa.avatar_texture_optimizer.editor.logging;
using net.fosa.avatar_texture_optimizer.editor.model;
using net.fosa.avatar_texture_optimizer.editor.progress;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.ndmf.passes
{
    // ========================================================================
    // Validate / 校验
    // ========================================================================

    /// <summary>
    /// Validates component placement: exactly one component per avatar, mounted
    /// on the VRCAvatarDescriptor object. Invalid placements abort the bake.
    /// 校验组件挂载：每个 Avatar 只允许一个组件，且挂在
    /// VRCAvatarDescriptor 对象上。不合规挂载中止烘焙。
    /// </summary>
    public sealed class ATOValidatePass : Pass<ATOValidatePass>
    {
        public override string DisplayName => "ATO: Validate";

        protected override void Execute(BuildContext context)
        {
            var state = GetOrCreateState(context);

            // 1. Must be attached to the avatar root (VRCAvatarDescriptor).
            //    必须挂载在 Avatar 根（VRCAvatarDescriptor）上。
            if (state.Component == null)
            {
                throw new InvalidOperationException(
                    "[ATO] No AvatarTextureOptimizer component found on the avatar root. / 在 Avatar 根上未找到 AvatarTextureOptimizer 组件。");
            }

#if NDMF_VRCSDK3_AVATARS
            var descriptor = context.AvatarRootObject.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                throw new InvalidOperationException(
                    "[ATO] AvatarTextureOptimizer must be attached to an object with a VRCAvatarDescriptor. / AvatarTextureOptimizer 必须挂载在带有 VRCAvatarDescriptor 的对象上。");
            }
#endif

            // 2. Only one component across the avatar (root + children).
            //    整个 Avatar（根+子级）只允许一个组件。
            var all = context.AvatarRootObject.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (all.Length > 1)
            {
                throw new InvalidOperationException(
                    "[ATO] Only one AvatarTextureOptimizer is allowed per avatar (root + children). / 每个 Avatar（含子级）只允许挂载一个 AvatarTextureOptimizer。");
            }

            if (!state.Component.Enabled)
            {
                ATOLog.Info("[ATO] Component disabled; skipping optimization. / 组件已禁用，跳过优化。");
                context.GetState<SkipOptimizationFlag>().Skip = true;
            }
        }
    }

    /// <summary>Internal flag to skip the rest of the pipeline. / 跳过其余流水线的内部标记。</summary>
    public sealed class SkipOptimizationFlag
    {
        public bool Skip;
    }

    // ========================================================================
    // Collect / 收集
    // ========================================================================

    /// <summary>
    /// Analysis pass: scan renderers/materials, scan animations, resolve the
    /// whitelist and deduplicate textures.
    /// 分析 pass：扫描渲染器/材质、扫描动画、解析白名单并去重贴图。
    /// </summary>
    public sealed class ATOCollectPass : Pass<ATOCollectPass>
    {
        public override string DisplayName => "ATO: Collect & Analyze";

        protected override void Execute(BuildContext context)
        {
            if (context.GetState<SkipOptimizationFlag>().Skip) return;
            var state = GetOrCreateState(context);
            var report = state.Report;
            report.BuildStarted();

            var avatarRoot = context.AvatarRootObject;
            var component = state.Component;
            ATOLog.Verbose = component.VerboseLogging;

            using var progress = new ATOBuildProgress(avatarRoot.name, 6);
            progress.Step("Validate / 校验");
            var validation = component.Validate();
            if (validation != null)
            {
                throw new InvalidOperationException("[ATO] " + validation);
            }

            progress.Step("Scan materials & UVs / 扫描材质与 UV");
            TextureCollector.Scan(avatarRoot, state);
            ATOLog.Info($"[ATO] Collected {state.AllUsages.Count} texture usages. / 收集到 {state.AllUsages.Count} 条贴图引用。");

            progress.Step("Scan animations / 扫描动画");
            var facts = AnimationScanner.Scan(avatarRoot, state);
            state.AnimationFacts = facts;
            AnimationScanner.MergeFacts(facts, state);
            ATOLog.Info($"[ATO] Animation facts: {facts.AnimatedTextureSwitches.Count} texture switches, " +
                        $"{facts.AnimatedEnabledRenderers.Count} enabled-by-anim renderers. / 动画事实：{facts.AnimatedTextureSwitches.Count} 次贴图切换，{facts.AnimatedEnabledRenderers.Count} 个动画启用渲染器。");
            if (progress.IsCancelled) { state.Cancelled = true; return; }

            progress.Step("Resolve whitelist / 解析白名单");
            WhitelistResolver.Resolve(avatarRoot, state);

            progress.Step("Deduplicate textures / 去重贴图");
            TextureDeduplicator.Deduplicate(state);
            ATOLog.Info($"[ATO] Deduplicated {state.TextureRemap.Count} textures. / 去重 {state.TextureRemap.Count} 张贴图。");
        }
    }

    // ========================================================================
    // Group / 分组
    // ========================================================================

    /// <summary>
    /// Builds UV groups (one per UV space) and type groups (texture partition
    /// for atlas packing). Textures from animation switches join the same UV
    /// group as the base texture of that space.
    /// 构建 UV 组（每个 UV 空间一个）与类型组（图集装箱的贴图划分）。
    /// 动画切换的贴图加入与该空间基础贴图相同的 UV 组。
    /// </summary>
    public sealed class ATOGroupPass : Pass<ATOGroupPass>
    {
        public override string DisplayName => "ATO: Build Groups";

        protected override void Execute(BuildContext context)
        {
            if (context.GetState<SkipOptimizationFlag>().Skip) return;
            var state = GetOrCreateState(context);
            if (state.Cancelled) { ATOLog.Warn("[ATO] Build cancelled; skipping remaining steps. / 构建已取消，跳过剩余步骤。"); return; }
            state.Report.BeginPhase("Grouping");

            // Group usages by UV space. / 按 UV 空间分组引用。
            var bySpace = new Dictionary<UVSpaceKey, List<TextureUsage>>();
            foreach (var usage in state.AllUsages)
            {
                if (usage.Texture == null) continue;
                if (state.WhitelistedTextures.Contains(usage.Texture)) continue; // whitelisted textures drive their UV group to whitelist below
                var key = new UVSpaceKey(usage.Renderer, usage.MaterialSlot, usage.UVChannel);
                if (!bySpace.TryGetValue(key, out var list))
                    bySpace[key] = list = new List<TextureUsage>();
                list.Add(usage);
            }

            // Build UV groups. / 构建 UV 组。
            foreach (var kv in bySpace)
            {
                var group = new UVGroup { Space = kv.Key };
                foreach (var usage in kv.Value)
                {
                    if (!group.Textures.Contains(usage)) group.Textures.Add(usage);
                }
                state.UVGroups.Add(group);
            }

            // Whitelisted / effectively-disabled renderers: their groups are
            // fully excluded. 白名单 / 实际未启用渲染器：其组被完全排除。
            state.UVGroups.RemoveAll(g =>
                state.WhitelistedRenderers.Contains(g.Space.Renderer) ||
                (state.AnimationFacts != null &&
                 !state.AnimationFacts.IsRendererEffectivelyEnabled(g.Space.Renderer)));

            // Whitelisted textures: their UV group skips atlasization (and, if
            // ALL textures of the group are whitelisted, everything).
            // 白名单贴图：其 UV 组跳过图集化（若组内全部贴图都被白名单，
            // 则完全跳过）。
            foreach (var group in state.UVGroups)
            {
                int whitelistedCount = group.Textures.Count(t => state.WhitelistedTextures.Contains(t.Texture));
                if (whitelistedCount == group.Textures.Count)
                {
                    group.Whitelisted = true;
                }
                else if (whitelistedCount > 0)
                {
                    // Same-UV other textures skip atlasization but still take
                    // part in whole-texture scaling + import optimization.
                    // 同 UV 的其他贴图跳过图集化，但仍参与整图缩放与导入优化。
                    group.SkippedAtlas = true;
                }
            }

            state.UVGroups.RemoveAll(g => g.Whitelisted);

            // Build type groups (partition textures for packing).
            // 构建类型组（为装箱划分贴图）。
            BuildTypeGroups(state);

            ATOLog.Info($"[ATO] Built {state.UVGroups.Count} UV groups, {state.TypeGroups.Count} type groups. / 构建了 {state.UVGroups.Count} 个 UV 组，{state.TypeGroups.Count} 个类型组。");
        }

        private static void BuildTypeGroups(ATOBuildState state)
        {
            // A texture belongs to exactly one type group keyed by
            // (companion flags, sRGB, filterMode). Companion flags are derived
            // from the usages: a texture referenced by a normal-map usage has a
            // Normal companion, etc. (Animation-switched textures join the
            // group of the base texture they replace.)
            // 一张贴图只属于一个按（伴随标志、sRGB、过滤模式）键控的类型组。
            // 伴随标志由引用推导：被法线引用引用的贴图具有 Normal 伴随等。
            // （动画切换的贴图加入它们所替换的基础贴图所在的组。）

            var groupsByKey = new Dictionary<(CompanionFlags, bool, FilterMode), TextureTypeGroup>();
            var textureGroup = new Dictionary<Texture2D, TextureTypeGroup>();

            // First pass: assign companion flags per texture.
            // 第一遍：为每张贴图分配伴随标志。
            var flags = new Dictionary<Texture2D, CompanionFlags>();
            foreach (var usage in state.AllUsages)
            {
                if (usage.Texture == null) continue;
                if (!flags.TryGetValue(usage.Texture, out var f)) f = CompanionFlags.None;
                switch (usage.Type)
                {
                    case TextureUsageType.NormalMap: f |= CompanionFlags.Normal; break;
                    case TextureUsageType.Mask: f |= CompanionFlags.Mask; break;
                }
                flags[usage.Texture] = f;
            }

            // Second pass: group by (flags, sRGB, filterMode).
            // 第二遍：按（标志、sRGB、过滤模式）分组。
            foreach (var kv in flags)
            {
                var tex = kv.Key;
                bool sRGB = true;
                var fm = FilterMode.Bilinear;
                // Take metadata from the first usage that references this tex.
                // 取引用该贴图的第一个引用的元数据。
                foreach (var usage in state.AllUsages)
                {
                    if (usage.Texture == tex)
                    {
                        sRGB = usage.IsSRGB;
                        fm = usage.FilterMode;
                        break;
                    }
                }

                var key = (kv.Value, sRGB, fm);
                if (!groupsByKey.TryGetValue(key, out var tg))
                {
                    tg = new TextureTypeGroup
                    {
                        Index = state.TypeGroups.Count,
                        Companions = kv.Value,
                        IsSRGB = sRGB,
                        FilterMode = fm,
                    };
                    groupsByKey[key] = tg;
                    state.TypeGroups.Add(tg);
                }
                tg.Textures.Add(tex);
                textureGroup[tex] = tg;
            }

            // Animation-switched textures: they are already covered above via
            // their own usages (FromAnimation), which carry the type of the
            // base usage — so they naturally join the matching group.
            // 动画切换的贴图：上面已通过它们自己的引用（FromAnimation）覆盖，
            // 这些引用带有基础引用的类型——因此它们自然加入匹配的组。

            // Compute per-group metadata. / 计算每组元数据。
            foreach (var tg in state.TypeGroups)
            {
                foreach (var tex in tg.Textures)
                {
                    bool hasAlpha = HasAlphaChannel(tex);
                    if (hasAlpha) tg.HasAlpha = true;
                }
            }
        }

        private static bool HasAlphaChannel(Texture2D tex)
        {
            if (tex == null) return false;
            var format = tex.format;
            switch (format)
            {
                case TextureFormat.RGBA32:
                case TextureFormat.RGBA4444:
                case TextureFormat.ARGB32:
                case TextureFormat.BGRA32:
                case TextureFormat.DXT5:
                case TextureFormat.BC7:
                case TextureFormat.ASTC_4x4:
                case TextureFormat.ASTC_5x5:
                case TextureFormat.ASTC_6x6:
                case TextureFormat.ASTC_8x8:
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.RGBAFloat:
                case TextureFormat.RGBAHalf:
                    return true;
                default:
                    return false;
            }
        }
    }

    // ========================================================================
    // Extract islands / 提取 UV 岛
    // ========================================================================

    /// <summary>
    /// Extracts UV islands (connected triangle components, merged when
    /// overlapping) from each UV group's mesh and UV channel.
    /// 从每个 UV 组的网格与 UV 通道提取 UV 岛（连通三角形分量，重叠时合并）。
    /// </summary>
    public sealed class ATOExtractIslandsPass : Pass<ATOExtractIslandsPass>
    {
        public override string DisplayName => "ATO: Extract Islands";

        protected override void Execute(BuildContext context)
        {
            if (context.GetState<SkipOptimizationFlag>().Skip) return;
            var state = GetOrCreateState(context);
            if (state.Cancelled) { ATOLog.Warn("[ATO] Build cancelled; skipping remaining steps. / 构建已取消，跳过剩余步骤。"); return; }
            state.Report.BeginPhase("ExtractIslands");
            IslandExtractor.Extract(state);
        }
    }

    // ========================================================================
    // Scale / 质量缩放
    // ========================================================================

    /// <summary>
    /// Scales UV islands (or whole textures when no atlas is generated) to the
    /// target quality using the quality metrics, then reports progress.
    /// 使用质量指标将 UV 岛（或不生成图集时的整张贴图）缩放到目标质量，
    /// 然后报告进度。
    /// </summary>
    public sealed class ATOScalePass : Pass<ATOScalePass>
    {
        public override string DisplayName => "ATO: Quality Scaling";

        protected override void Execute(BuildContext context)
        {
            if (context.GetState<SkipOptimizationFlag>().Skip) return;
            var state = GetOrCreateState(context);
            if (state.Cancelled) { ATOLog.Warn("[ATO] Build cancelled; skipping remaining steps. / 构建已取消，跳过剩余步骤。"); return; }
            // Density correction must run AFTER UV groups exist (blend shapes
            // 0/100 max + animated max scale). / 密度修正必须在 UV 组构建之后
            // 运行（形态键 0/100 最大值 + 动画最大缩放）。
            if (state.AnimationFacts != null)
                DensityAnalyzer.Correct(state, state.AnimationFacts);
            state.Report.BeginPhase("Scale");
            quality.IslandScaler.Scale(state);
        }
    }

    // ========================================================================
    // Pack / 装箱
    // ========================================================================

    /// <summary>
    /// Packs islands into atlas layouts (Burst raster + BLF + candidate pool).
    /// 将岛装箱进图集布局（Burst 光栅化 + BLF + 候选池）。
    /// </summary>
    public sealed class ATOPackPass : Pass<ATOPackPass>
    {
        public override string DisplayName => "ATO: Pack Atlases";

        protected override void Execute(BuildContext context)
        {
            if (context.GetState<SkipOptimizationFlag>().Skip) return;
            var state = GetOrCreateState(context);
            if (state.Cancelled) { ATOLog.Warn("[ATO] Build cancelled; skipping remaining steps. / 构建已取消，跳过剩余步骤。"); return; }
            state.Report.BeginPhase("Pack");
            atlas.Packer.Pack(state);
        }
    }

    // ========================================================================
    // Build atlas textures / 生成图集贴图
    // ========================================================================

    /// <summary>
    /// Creates the actual atlas textures from the packed layouts (GPU resample
    /// + pull-push fill) and applies import settings.
    /// 根据装箱布局创建实际图集贴图（GPU 重采样 + pull-push 填充）并应用
    /// 导入参数。
    /// </summary>
    public sealed class ATOBuildAtlasesPass : Pass<ATOBuildAtlasesPass>
    {
        public override string DisplayName => "ATO: Build Atlas Textures";

        protected override void Execute(BuildContext context)
        {
            if (context.GetState<SkipOptimizationFlag>().Skip) return;
            var state = GetOrCreateState(context);
            if (state.Cancelled) { ATOLog.Warn("[ATO] Build cancelled; skipping remaining steps. / 构建已取消，跳过剩余步骤。"); return; }
            state.Report.BeginPhase("BuildAtlases");
            atlas.AtlasBuilder.Build(context, state);
        }
    }

    // ========================================================================
    // Apply / 应用
    // ========================================================================

    /// <summary>
    /// Applies the new UVs to meshes (with AAO UV evacuation), reassigns
    /// textures on materials, and updates animation references.
    /// 将新 UV 应用到网格（含 AAO UV 疏散）、在材质上重新赋贴图并更新动画
    /// 引用。
    /// </summary>
    public sealed class ATOApplyPass : Pass<ATOApplyPass>
    {
        public override string DisplayName => "ATO: Apply";

        protected override void Execute(BuildContext context)
        {
            if (context.GetState<SkipOptimizationFlag>().Skip) return;
            var state = GetOrCreateState(context);
            if (state.Cancelled) { ATOLog.Warn("[ATO] Build cancelled; skipping remaining steps. / 构建已取消，跳过剩余步骤。"); return; }
            state.Report.BeginPhase("Apply");
            apply.Applier.Apply(context, state);
        }
    }

    // ========================================================================
    // Finalize / 收尾
    // ========================================================================

    /// <summary>
    /// Deduplicates identical materials (merging slots when safe), removes the
    /// ATO component from the baked avatar, and prints the report.
    /// 对相同材质去重（安全时合并材质槽）、从烘焙成品上移除 ATO 组件并输出
    /// 报告。
    /// </summary>
    public sealed class ATOFinalizePass : Pass<ATOFinalizePass>
    {
        public override string DisplayName => "ATO: Finalize";

        protected override void Execute(BuildContext context)
        {
            if (context.GetState<SkipOptimizationFlag>().Skip) return;
            var state = GetOrCreateState(context);
            if (state.Cancelled) { ATOLog.Warn("[ATO] Build cancelled; skipping remaining steps. / 构建已取消，跳过剩余步骤。"); return; }
            state.Report.BeginPhase("Finalize");
            apply.Deduplicator.Finalize(context, state);
        }
    }

    // ========================================================================
    // Shared helpers / 共享辅助
    // ========================================================================

    /// <summary>
    /// Get or create the per-build ATO state from the NDMF BuildContext.
    /// 从 NDMF BuildContext 获取或创建每次构建的 ATO 状态。
    /// </summary>
    public static class ATOBuildContextHelper
    {
        public static ATOBuildState GetOrCreateState(this BuildContext context)
        {
            var state = context.GetState<ATOBuildState>(ctx =>
            {
                var s = new ATOBuildState();
                var comp = ctx.AvatarRootObject.GetComponent<AvatarTextureOptimizer>();
                s.Component = comp;
                s.Platform = model.ATOBuildState.PlatformFor(EditorUserBuildSettings.activeBuildTarget);
                return s;
            });
            return state;
        }
    }
}
