// ATOAPI — public extension surface for advanced users & third-party developers / 面向高级用户与第三方开发者的扩展接口
// Reserved hooks: shader analyzers, pipeline stage events, i18n tables. These are stable entry points;
// internal pipeline structures remain internal on purpose.<br>
// 预留扩展点：着色器分析器、流水线阶段事件、i18n 语言表。内部流水线结构刻意保持 internal。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>Semantic class of a texture slot / 贴图槽语义类别。</summary>
    public enum ATOTextureClass { Albedo = 0, Normal = 1, Mask = 2 }

    /// <summary>
    /// One texture slot discovered on a material. Analyzers fill this in; ATO applies its own
    /// ST/rotation/animation safety guards afterwards.<br/>
    /// 材质上的一个贴图槽。分析器填充后 ATO 还会施加自身的 ST/旋转/动画安全守卫。
    /// </summary>
    public struct ATOTextureSlot
    {
        public string property;            // texture property name / 贴图属性名
        public ATOTextureClass cls;        // semantic class / 语义类别
        public int uvChannel;              // sampled UV channel / 采样UV通道
        public bool safe;                  // false → treat as whitelist / false 则视为白名单处理
        public string unsafeReason;        // reason when unsafe / 不安全原因
        public int maskChannelFlags;       // Mask class only: used RGBA channel bits / 仅蒙版：使用通道位
    }

    /// <summary>
    /// Third-party shader analyzer. Register via <see cref="ATOShaderAnalyzerRegistry"/>.<br/>
    /// 第三方着色器分析器，通过 <see cref="ATOShaderAnalyzerRegistry"/> 注册（优先于内置分析器执行）。
    /// </summary>
    public interface IATOShaderAnalyzer
    {
        /// <summary>Whether this analyzer understands the shader / 是否认识该着色器。</summary>
        bool CanAnalyze(Shader shader);
        /// <summary>Enumerate texture slots of the material / 枚举材质的贴图槽。</summary>
        void Analyze(Material material, List<ATOTextureSlot> output);
    }

    /// <summary>Registry for custom shader analyzers (prepended before built-ins). / 自定义着色器分析器注册表（置于内置之前）。</summary>
    public static class ATOShaderAnalyzerRegistry
    {
        private static readonly List<IATOShaderAnalyzer> _custom = new List<IATOShaderAnalyzer>();
        public static void Register(IATOShaderAnalyzer analyzer) { if (analyzer != null && !_custom.Contains(analyzer)) _custom.Insert(0, analyzer); }
        public static void Unregister(IATOShaderAnalyzer analyzer) { _custom.Remove(analyzer); }
        internal static IReadOnlyList<IATOShaderAnalyzer> Custom => _custom;
    }

    /// <summary>Public summary used by pipeline events / 流水线事件用的公开摘要。</summary>
    public sealed class ATOEventArgs
    {
        public string stage;           // stage name / 阶段名
        public int textureCount;       // textures in scope / 贴图数量
        public int islandCount;        // uv islands / UV岛数量
        public int atlasCount;         // generated atlases / 图集数量
        public UnityEngine.GameObject avatar; // avatar root (build copy) / Avatar根（构建副本）
    }

    /// <summary>Pipeline stage events for third-party tooling. / 面向第三方工具的流水线阶段事件。</summary>
    public static class ATOEvents
    {
        /// <summary>Raised when a stage finished; stage in {discovery, uv, quality, packing, bake, remap, materials, clips, report}.</summary>
        public static event Action<ATOEventArgs> StageFinished;
        internal static void Raise(string stage, ATOPipeContext pipe, GameObject avatar)
        {
            try
            {
                StageFinished?.Invoke(new ATOEventArgs
                {
                    stage = stage, avatar = avatar,
                    textureCount = pipe?.textures.Count ?? 0,
                    islandCount = pipe?.islands.Count ?? 0,
                    atlasCount = pipe?.atlases.Count ?? 0,
                });
            }
            catch (Exception e) { ATOLog.Warn($"external event handler failed: {e.Message}"); }
        }
    }

    /// <summary>Generic pipeline hook interface (reserved). / 通用流水线钩子接口（预留）。</summary>
    public interface IATOPipelineHook { void OnStage(string stageName, object pipelineContext); }

    public static class ATOHookRegistry
    {
        private static readonly List<IATOPipelineHook> _hooks = new List<IATOPipelineHook>();
        public static void Register(IATOPipelineHook hook) { if (hook != null && !_hooks.Contains(hook)) _hooks.Add(hook); }
        public static void Unregister(IATOPipelineHook hook) { _hooks.Remove(hook); }
        internal static void Notify(string stage, object ctx)
        {
            foreach (var h in _hooks) { try { h.OnStage(stage, ctx); } catch (Exception e) { ATOLog.Warn($"hook failed: {e.Message}"); } }
        }
    }
}
