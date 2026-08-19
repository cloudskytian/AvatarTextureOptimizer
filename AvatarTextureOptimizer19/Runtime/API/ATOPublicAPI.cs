// English: Public extension surface for advanced users and third-party developers.
// 中文：面向高级用户与第三方开发者的扩展接口。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.API
{
    /// <summary>
    /// Result of analyzing one material texture slot.
    /// 单个材质贴图槽的分析结果。
    /// </summary>
    public sealed class ATOTextureSlotInfo
    {
        public Material Material;
        public string PropertyName;
        public Texture2D Texture;
        public int UvChannel;
        public bool HasTransform;
        public bool IsMeshSampled;
        public bool IsSpecialPurpose;
        public ATOTextureSemantic Semantic;
        public ATOCompanionKind Companions;
        public ATOAlphaMode AlphaMode;
        public float Cutoff;
        public TextureWrapMode WrapMode;
        public FilterMode FilterMode;
        public bool LinearColorSpace;
        public string Warning;
    }

    /// <summary>
    /// Third-party shader analyzer. Return null to abstain; throw / set Warning to skip as whitelist.
    /// 第三方着色器分析器。返回 null 表示不处理；给出 Warning 则按白名单跳过。
    /// </summary>
    public interface IATOShaderAnalyzer
    {
        string Id { get; }

        /// <summary>Higher runs first. Built-in lilToon = 100, standard keywords = 10.</summary>
        int Priority { get; }

        bool CanAnalyze(Material material);

        IReadOnlyList<ATOTextureSlotInfo> Analyze(Material material);
    }

    /// <summary>
    /// Filter or rewrite islands before packing.
    /// 装箱前过滤或改写 UV 岛。
    /// </summary>
    public interface IATOIslandProcessor
    {
        string Id { get; }
        void Process(ATOExtensionContext context);
    }

    /// <summary>
    /// Observes pipeline stages for custom reporting or extra work.
    /// 观察流水线阶段，便于自定义报告或附加工作。
    /// </summary>
    public interface IATOPipelineHook
    {
        string Id { get; }
        void OnStage(string stage, ATOExtensionContext context);
    }

    /// <summary>
    /// Read-only context handed to extensions. Do not retain past the bake.
    /// 交给扩展的只读上下文。不要在烘焙结束后继续持有。
    /// </summary>
    public sealed class ATOExtensionContext
    {
        public GameObject AvatarRoot { get; internal set; }
        public AvatarTextureOptimizer Component { get; internal set; }
        public ATOBuildPlatform Platform { get; internal set; }
        public object PipelineState { get; internal set; }
        public Action<string> Log { get; internal set; }
    }

    /// <summary>
    /// Global registry. Register from InitializeOnLoad.
    /// 全局注册表。请在 InitializeOnLoad 中注册。
    /// </summary>
    public static class ATOExtensionRegistry
    {
        private static readonly List<IATOShaderAnalyzer> ShaderAnalyzers = new List<IATOShaderAnalyzer>();
        private static readonly List<IATOIslandProcessor> IslandProcessors = new List<IATOIslandProcessor>();
        private static readonly List<IATOPipelineHook> Hooks = new List<IATOPipelineHook>();

        public static void Register(IATOShaderAnalyzer analyzer)
        {
            if (analyzer == null) return;
            if (!ShaderAnalyzers.Contains(analyzer)) ShaderAnalyzers.Add(analyzer);
        }

        public static void Register(IATOIslandProcessor processor)
        {
            if (processor == null) return;
            if (!IslandProcessors.Contains(processor)) IslandProcessors.Add(processor);
        }

        public static void Register(IATOPipelineHook hook)
        {
            if (hook == null) return;
            if (!Hooks.Contains(hook)) Hooks.Add(hook);
        }

        public static void Unregister(IATOShaderAnalyzer analyzer)
        {
            ShaderAnalyzers.Remove(analyzer);
        }

        public static void Unregister(IATOIslandProcessor processor)
        {
            IslandProcessors.Remove(processor);
        }

        public static void Unregister(IATOPipelineHook hook)
        {
            Hooks.Remove(hook);
        }

        public static IReadOnlyList<IATOShaderAnalyzer> GetShaderAnalyzers()
        {
            return ShaderAnalyzers;
        }

        public static IReadOnlyList<IATOIslandProcessor> GetIslandProcessors()
        {
            return IslandProcessors;
        }

        public static IReadOnlyList<IATOPipelineHook> GetHooks()
        {
            return Hooks;
        }
    }
}
