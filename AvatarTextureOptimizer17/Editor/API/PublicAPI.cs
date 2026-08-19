// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// API/PublicAPI.cs — 公共扩展接口 / Public extension API
//
// 需求: 对各功能预留接口方便高级用户自定义扩展功能和第三方开发者进行开发。
// 说明: 全部为可选扩展点；未注册时走内置实现。注册后需在 Build 前生效（InitializeOnLoad）。
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.api
{
    /// <summary>
    /// 着色器贴图属性分类扩展：内置表与通用关键字无法识别时回调。
    /// Shader texture property classifier extension (called when built-in tables fail).
    /// </summary>
    public interface IATOTextureClassifier
    {
        /// <summary>返回角色或 null（不处理）/ return a role or null to skip</summary>
        TextureRole? Classify(Material material, string property);
    }

    /// <summary>
    /// 额外质量指标扩展：在构建时追加一个必须达标的检查。
    /// Extra quality metric: an additional check that must pass during scaling.
    /// </summary>
    public interface IATOQualityMetric
    {
        /// <summary>检查名称（日志用）/ metric name (for logging)</summary>
        string Name { get; }

        /// <summary>
        /// 评估候选。orig/cand 为线性 RGBA；返回是否达标。
        /// Evaluate a candidate. orig/cand are linear RGBA; return whether it passes.
        /// </summary>
        bool Evaluate(TextureRef tref, float[] orig, float[] cand, int width, int height);
    }

    /// <summary>
    /// 装箱策略扩展：在每次 UV 组装箱前回调（可拒绝并让内置逻辑处理）。
    /// Atlas strategy hook: called before each UV group is packed (can veto).
    /// </summary>
    public interface IATOAtlasStrategy
    {
        /// <summary>返回 false 表示拒绝该组（按内置逻辑处理）/ return false to veto the group</summary>
        bool CanPack(UVGroup group);
    }

    /// <summary>
    /// 管线阶段钩子 / Pipeline stage hooks.
    /// </summary>
    public interface IATOPipelineHook
    {
        void OnStageBegin(string stage);
        void OnStageEnd(string stage);
    }

    /// <summary>
    /// 公共 API 注册表 / Public API registry.
    /// </summary>
    public static class ATOPublicAPI
    {
        private static readonly List<IATOTextureClassifier> _classifiers = new List<IATOTextureClassifier>();
        private static readonly List<IATOQualityMetric> _metrics = new List<IATOQualityMetric>();
        private static readonly List<IATOAtlasStrategy> _strategies = new List<IATOAtlasStrategy>();
        private static readonly List<IATOPipelineHook> _hooks = new List<IATOPipelineHook>();

        public static void RegisterClassifier(IATOTextureClassifier c) => _classifiers.Add(c);
        public static void RegisterQualityMetric(IATOQualityMetric m) => _metrics.Add(m);
        public static void RegisterAtlasStrategy(IATOAtlasStrategy s) => _strategies.Add(s);
        public static void RegisterPipelineHook(IATOPipelineHook h) => _hooks.Add(h);

        public static IReadOnlyList<IATOTextureClassifier> Classifiers => _classifiers;
        public static IReadOnlyList<IATOQualityMetric> QualityMetrics => _metrics;
        public static IReadOnlyList<IATOAtlasStrategy> AtlasStrategies => _strategies;
        public static IReadOnlyList<IATOPipelineHook> PipelineHooks => _hooks;

        internal static void Clear()
        {
            _classifiers.Clear();
            _metrics.Clear();
            _strategies.Clear();
            _hooks.Clear();
        }
    }
}
