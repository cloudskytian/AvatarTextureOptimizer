using System;
using System.Collections.Generic;
using NetFosa.AvatarTextureOptimizer.Editor.Analysis;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using NetFosa.AvatarTextureOptimizer.Editor.Utils;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// 构建上下文：根对象、组件、有效设置、日志/报告、缓存、取消标记。
    /// </summary>
    public sealed class ATOContext : IDisposable
    {
        public readonly GameObject Root;
        public readonly AvatarTextureOptimizer Component;
        public readonly EffectiveSettings Settings;
        public readonly ATOLogger Logger;
        public readonly BuildReport Report;
        public readonly TextureCache Cache;
        public readonly RenderTexturePool RtPool;

        public AnimationAnalysis Animation;
        public AvatarScanner Scanner;
        public TextureMappingBuilder Mapping;

        public readonly List<string> InfoMessages = new List<string>();

        /// <summary>整个 Avatar 上是否有多于一个 ATO 组件（烘焙前校验用）。</summary>
        public bool HasMultipleComponents;

        public ATOContext(GameObject root, AvatarTextureOptimizer component, EffectiveSettings settings,
            ATOLogger logger, BuildReport report)
        {
            Root = root;
            Component = component;
            Settings = settings;
            Logger = logger;
            Report = report;
            Cache = new TextureCache();
            RtPool = new RenderTexturePool();
        }

        public void Dispose()
        {
            Cache.Dispose();
            RtPool.Dispose();
        }
    }
}
