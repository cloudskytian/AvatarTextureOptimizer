// AvatarTextureOptimizer
// File: Editor/Model/ATOBuildState.cs
//
// The central per-build state, stored in the NDMF BuildContext via GetState.
// Holds every structure the pipeline produces: scanned usages, UV groups,
// type groups, atlas entries, the active platform, and the final report.
//
// 每次构建的中央状态，通过 NDMF BuildContext 的 GetState 存储。保存流水线
// 产生的全部结构：扫描到的引用、UV 组、类型组、图集条目、当前平台与报告。

using System.Collections.Generic;
using net.fosa.avatar_texture_optimizer.editor.logging;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.model
{
    /// <summary>
    /// Which platform the current build targets (drives platform overrides).
    /// 当前构建目标平台（驱动平台覆写）。
    /// </summary>
    public enum ATOBuildPlatform
    {
        PC,
        Android,
        iOS,
        Unknown,
    }

    /// <summary>
    /// All mutable state of one bake. One instance per avatar build.
    /// 一次烘焙的全部可变状态。每个 Avatar 构建一个实例。
    /// </summary>
    public sealed class ATOBuildState
    {
        // ---- Inputs / 输入 ----
        /// <summary>The component driving this bake. / 驱动本次烘焙的组件。</summary>
        public AvatarTextureOptimizer Component;

        /// <summary>Target platform of the current build. / 当前构建的目标平台。</summary>
        public ATOBuildPlatform Platform = ATOBuildPlatform.Unknown;

        // ---- Analysis results / 分析结果 ----
        /// <summary>All texture usages collected from materials and animations. / 从材质与动画收集到的全部贴图引用。</summary>
        public readonly List<TextureUsage> AllUsages = new List<TextureUsage>();

        /// <summary>All UV groups (one per UV space with optimizable textures). / 全部 UV 组（每个可优化 UV 空间一个）。</summary>
        public readonly List<UVGroup> UVGroups = new List<UVGroup>();

        /// <summary>All type groups (partition of textures for atlas packing). / 全部类型组（贴图按图集装箱要求划分）。</summary>
        public readonly List<TextureTypeGroup> TypeGroups = new List<TextureTypeGroup>();

        /// <summary>Planned/created atlas entries. / 计划/已创建的图集条目。</summary>
        public readonly List<AtlasEntry> Atlases = new List<AtlasEntry>();

        /// <summary>Canonical global layouts (positions shared by type-group atlases). / 规范全局布局（位置被类型组图集共享）。</summary>
        public readonly List<PackerLayoutRef> Layouts = new List<PackerLayoutRef>();

        // ---- Texture dedup / 贴图去重 ----
        /// <summary>Map from original texture to its deduplicated representative. / 原贴图到去重后代表贴图的映射。</summary>
        public readonly Dictionary<Texture2D, Texture2D> TextureRemap = new Dictionary<Texture2D, Texture2D>();

        // ---- Whitelist / 白名单 ----
        /// <summary>Textures that must skip ALL optimization. / 必须跳过所有优化的贴图。</summary>
        public readonly HashSet<Texture2D> WhitelistedTextures = new HashSet<Texture2D>();

        /// <summary>Renderers excluded from optimization. / 被排除优化的渲染器。</summary>
        public readonly HashSet<Renderer> WhitelistedRenderers = new HashSet<Renderer>();

        // ---- Mesh processing bookkeeping / 网格处理记账 ----
        /// <summary>Mesh -> new UV data (channel 0..7). / 网格 -> 新 UV 数据（通道 0..7）。</summary>
        public readonly Dictionary<Mesh, Vector2[][]> NewUVs = new Dictionary<Mesh, Vector2[][]>();

        /// <summary>Renderer -> UV channel that held position data, for evacuation bookkeeping. / 渲染器 -> 保存位置数据的 UV 通道（用于疏散记账）。</summary>
        public readonly Dictionary<Renderer, int> EvacuatedChannel = new Dictionary<Renderer, int>();

        // ---- Reports & misc / 报告与杂项 ----
        /// <summary>Structured build report. / 结构化烘焙报告。</summary>
        public readonly BuildReport Report = new BuildReport();

        /// <summary>Warnings accumulated during the bake. / 烘焙期间累积的警告。</summary>
        public readonly List<string> Warnings = new List<string>();

        /// <summary>True when the user cancelled the bake (graceful early-exit flag). / 用户取消烘焙时为 true（优雅提前退出标记）。</summary>
        public bool Cancelled;

        /// <summary>New materials created during the bake (for dedup at the end). / 烘焙期间创建的新材质（用于最后的去重）。</summary>
        public readonly List<Material> NewMaterials = new List<Material>();

        /// <summary>New textures created during the bake. / 烘焙期间创建的新贴图。</summary>
        public readonly List<Texture2D> NewTextures = new List<Texture2D>();

        /// <summary>Source-texture -> atlas entries it contributed to (report). / 源贴图 -> 其贡献的图集条目（报告用）。</summary>
        public readonly Dictionary<Texture2D, List<AtlasEntry>> TextureToAtlases = new Dictionary<Texture2D, List<AtlasEntry>>();

        /// <summary>Whole-texture target sizes when no atlas is generated. / 不生成图集时的整贴图目标尺寸。</summary>
        public readonly Dictionary<Texture2D, Vector2Int> WholeTextureScale = new Dictionary<Texture2D, Vector2Int>();

        /// <summary>Whole-texture scaled copies (texture -> copy). / 整图缩放副本（贴图 -> 副本）。</summary>
        public readonly Dictionary<Texture2D, Texture2D> WholeTextureCopies = new Dictionary<Texture2D, Texture2D>();

        /// <summary>Material dedup remap (original -> representative). / 材质去重重映射（原材质 -> 代表材质）。</summary>
        public readonly Dictionary<Material, Material> MaterialRemap = new Dictionary<Material, Material>();

        /// <summary>Material-slot merge map per renderer: (renderer, oldSlot) -> newSlot. / 按渲染器的材质槽合并映射：(渲染器, 旧槽) -> 新槽。</summary>
        public readonly Dictionary<(Renderer, int), int> MaterialSlotMerge = new Dictionary<(Renderer, int), int>();

        /// <summary>Renderers whose material slots are individually animated (slot merging is skipped for them). / 材质槽被单独动画的渲染器（对它们跳过槽合并）。</summary>
        public readonly HashSet<Renderer> AnimatedMaterialSlotRenderers = new HashSet<Renderer>();

        /// <summary>Animation facts gathered during the collect pass. / 收集阶段获取的动画事实。</summary>
        public analysis.AnimationFacts AnimationFacts;

        /// <summary>Log a warning into both the console and the report. / 同时将警告写入控制台与报告。</summary>
        public void Warn(string message)
        {
            Warnings.Add(message);
            logging.ATOLog.Warn(message);
        }

        /// <summary>Resolve the effective platform for a given component. / 解析组件对应的有效平台。</summary>
        public static ATOBuildPlatform PlatformFor(UnityEditor.BuildTarget target)
        {
            switch (target)
            {
                case UnityEditor.BuildTarget.Android: return ATOBuildPlatform.Android;
                case UnityEditor.BuildTarget.iOS: return ATOBuildPlatform.iOS;
                case UnityEditor.BuildTarget.StandaloneWindows:
                case UnityEditor.BuildTarget.StandaloneWindows64:
                case UnityEditor.BuildTarget.StandaloneOSX:
                case UnityEditor.BuildTarget.StandaloneLinux64:
                    return ATOBuildPlatform.PC;
                default: return ATOBuildPlatform.Unknown;
            }
        }
    }

    /// <summary>
    /// Lightweight layout reference stored in state (mirrors the packer's
    /// layout without exposing internal types across namespaces).
    /// 存储在状态中的轻量布局引用（镜像装箱器的布局，避免跨命名空间暴露
    /// 内部类型）。
    /// </summary>
    public sealed class PackerLayoutRef
    {
        public int Width, Height;
        public List<UVGroup> Groups = new List<UVGroup>();
    }
}
