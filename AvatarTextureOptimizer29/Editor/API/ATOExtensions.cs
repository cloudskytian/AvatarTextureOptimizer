// Public extension API for advanced users & third-party developers.
// 面向高级用户与第三方开发者的公开扩展 API。
// Hooks run synchronously inside the ATO pass; exceptions abort the bake.
// 钩子在 ATO pass 内同步执行；抛出异常将中止烘焙。

using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>Pipeline stage identifiers. / 流水线阶段标识。</summary>
    public enum ATOStage
    {
        BeforeScan,    // before renderer/material scan / 扫描前
        AfterAnalysis, // usage graph & islands ready / 使用图与岛就绪
        AfterQuality,  // island scales decided / 岛缩放已定
        AfterPack,     // atlas layouts decided / 图集布局已定
        AfterApply,    // meshes/materials/textures rewritten / 网格材质贴图已重写
        Finish,        // before component removal & report / 移除组件与报告前
    }

    /// <summary>Read/write surface handed to extensions. / 传给扩展的读写面。</summary>
    public sealed class ATOStageContext
    {
        /// <summary>NDMF build context. / NDMF 构建上下文。</summary>
        public BuildContext Build { get; }

        /// <summary>The ATO component (settings). / ATO 组件（配置）。</summary>
        public AvatarTextureOptimizer Component { get; }

        /// <summary>Effective platform. / 生效平台。</summary>
        public AtoPlatform Platform { get; }

        /// <summary>Warnings collected so far (mutable). / 已收集的警告（可追加）。</summary>
        public IList<string> Warnings { get; }

        internal ATOStageContext(BuildContext build, AvatarTextureOptimizer component, AtoPlatform platform,
            IList<string> warnings)
        {
            Build = build;
            Component = component;
            Platform = platform;
            Warnings = warnings;
        }
    }

    /// <summary>Base class for ATO extensions. Override what you need.
    /// ATO 扩展基类，按需重写。</summary>
    public abstract class ATOExtension
    {
        /// <summary>Lower runs earlier. / 数值小先运行。</summary>
        public virtual int Priority => 0;

        public virtual void OnStage(ATOStage stage, ATOStageContext ctx) { }
    }

    public static class ATOExtensionRegistry
    {
        private static readonly List<ATOExtension> Extensions = new List<ATOExtension>();

        public static void Register(ATOExtension ext)
        {
            if (ext == null) return;
            Extensions.Add(ext);
            Extensions.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        public static void Unregister(ATOExtension ext) => Extensions.Remove(ext);

        public static IReadOnlyList<ATOExtension> All => Extensions;

        internal static void Emit(ATOStage stage, ATOStageContext ctx)
        {
            foreach (var ext in Extensions)
            {
                try
                {
                    ext.OnStage(stage, ctx);
                }
                catch (Exception e)
                {
                    ATOLog.Error($"extension {ext.GetType().Name} failed at {stage}: {e.Message}\n{e.StackTrace}");
                }
            }
        }
    }
}
