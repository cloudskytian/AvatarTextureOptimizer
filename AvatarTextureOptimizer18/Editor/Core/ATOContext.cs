using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    // 单次烘焙的共享上下文：NDMF 上下文、解析后的设置、分析结果、报告。
    // Shared context of a single build: NDMF context, resolved settings, analysis results, report.
    internal sealed class ATOContext
    {
        public BuildContext ndmf;
        public ATOReport report = new ATOReport();

        public GameObject avatarRoot;
        public VRCAvatarDescriptor descriptor;
        public ATOAvatar component;
        public ATOPlatform platform;

        // 解析后的设置（已应用平台覆盖）。Resolved settings (platform overrides applied).
        public ATOSettings settings;
        public ATOMetricThresholds metrics;
        public ATOFormatSettings formats;

        // 分析结果。Analysis results.
        public readonly List<SlotEntry> slots = new List<SlotEntry>();
        public readonly List<Renderer> renderers = new List<Renderer>();
        public readonly List<Material> materials = new List<Material>();
        public readonly AnimationAnalysis animations = new AnimationAnalysis();
        public readonly List<TextureEntry> textures = new List<TextureEntry>();
        public readonly Dictionary<Texture2D, TextureEntry> textureMap = new Dictionary<Texture2D, TextureEntry>();

        // 岛实体（按网格+通道索引）。Island entities (indexed by mesh+channel).
        public readonly List<Islands.IslandEntity> islandEntities = new List<Islands.IslandEntity>();
        public readonly Dictionary<KeyValuePair<Mesh, int>, List<Islands.IslandEntity>> entityByKey =
            new Dictionary<KeyValuePair<Mesh, int>, List<Islands.IslandEntity>>();
        // 类型组（装箱阶段构建）。Type groups (built at the packing stage).
        public readonly List<Islands.TypeGroup> typeGroups = new List<Islands.TypeGroup>();
        // 全部图集计划（装箱阶段产出，图集构建阶段消费）。All atlas plans (produced by packing, consumed by atlas building).
        public readonly List<Packing.AtlasPlan> atlasPlans = new List<Packing.AtlasPlan>();
        // 网格替换映射（旧网格 → 新网格；网格/槽位合并阶段复用）。Mesh replacement map (old → new; reused by slot merge).
        public readonly Dictionary<Mesh, Mesh> meshReplacements = new Dictionary<Mesh, Mesh>();

        public ATOContext(BuildContext ndmfContext, GameObject root, VRCAvatarDescriptor desc, ATOAvatar comp)
        {
            ndmf = ndmfContext;
            avatarRoot = root;
            descriptor = desc;
            component = comp;
            settings = comp.settings;
            if (settings == null) settings = new ATOSettings();
            settings.Normalize();
            platform = ATOPlatformUtil.ResolveCurrentPlatform();
            metrics = settings.ResolveMetrics(platform);
            formats = settings.ResolveFormats(platform);
            report.avatarName = root != null ? root.name : "?";
        }

        // 检查取消。Throws ATOCancelledException when cancellation was requested.
        public void CheckCancelled()
        {
            if (ATOCancellation.Requested) throw new ATOCancelledException();
        }

        // 更新进度（0~1 全局进度）。Updates global progress.
        public bool Progress(string stageTitle, string info, float progress01)
        {
            return ATOCancellation.Update(stageTitle, info, progress01);
        }
    }

    // 平台解析：参考 Unity 平台设置；默认读取当前构建平台。
    // Platform resolution: mirrors Unity's build target; defaults to the current build target.
    internal static class ATOPlatformUtil
    {
        public static ATOPlatform ResolveCurrentPlatform()
        {
            switch (UnityEditor.EditorUserBuildSettings.activeBuildTarget)
            {
                case UnityEditor.BuildTarget.Android: return ATOPlatform.Android;
                case UnityEditor.BuildTarget.iOS: return ATOPlatform.iOS;
                default: return ATOPlatform.PC;
            }
        }

        // 平台 → Unity BuildTargetGroup（用于导入设置）。Platform → Unity BuildTargetGroup (for import settings).
        public static UnityEditor.BuildTargetGroup ToBuildTargetGroup(ATOPlatform platform)
        {
            switch (platform)
            {
                case ATOPlatform.Android: return UnityEditor.BuildTargetGroup.Android;
                case ATOPlatform.iOS: return UnityEditor.BuildTargetGroup.iOS;
                default: return UnityEditor.BuildTargetGroup.Standalone;
            }
        }

        // 平台 → TextureImporterPlatformSettings 的平台名。Platform → TextureImporterPlatformSettings platform name.
        public static string ToImporterPlatformName(ATOPlatform platform)
        {
            switch (platform)
            {
                case ATOPlatform.Android: return "Android";
                case ATOPlatform.iOS: return "iPhone";
                default: return "Standalone";
            }
        }
    }
}
