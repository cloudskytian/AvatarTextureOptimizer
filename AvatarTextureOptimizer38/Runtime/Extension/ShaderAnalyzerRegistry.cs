using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Extension point for custom shader analyzers. / 自定义着色器分析器扩展点。
    /// </summary>
    public static class ShaderAnalyzerRegistry
    {
        private static readonly List<IShaderAnalyzer> Analyzers = new List<IShaderAnalyzer>();

        public static void Register(IShaderAnalyzer analyzer)
        {
            if (analyzer == null) throw new ArgumentNullException(nameof(analyzer));
            if (!Analyzers.Contains(analyzer))
            {
                Analyzers.Add(analyzer);
                Analyzers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                Debug.Log($"{AvatarTextureOptimizer.LogPrefix} Registered shader analyzer: {analyzer.Name} (priority {analyzer.Priority})");
            }
        }

        public static void Unregister(IShaderAnalyzer analyzer)
        {
            if (analyzer == null) return;
            Analyzers.Remove(analyzer);
        }

        public static IReadOnlyList<IShaderAnalyzer> GetAnalyzers() => Analyzers;
    }

    /// <summary>
    /// Hook invoked around the optimize pipeline. / 优化管线前后钩子。
    /// </summary>
    public interface IAtoOptimizeHook
    {
        int Priority { get; }
        string Name { get; }
        void OnBeforeOptimize(GameObject avatarRoot, AvatarTextureOptimizer component);
        void OnAfterOptimize(GameObject avatarRoot, AvatarTextureOptimizer component);
    }

    /// <summary>
    /// Registry for IAtoOptimizeHook. / 优化钩子注册表。
    /// </summary>
    public static class AtoHookRegistry
    {
        private static readonly List<IAtoOptimizeHook> Hooks = new List<IAtoOptimizeHook>();

        public static void Register(IAtoOptimizeHook hook)
        {
            if (hook == null) throw new ArgumentNullException(nameof(hook));
            if (!Hooks.Contains(hook))
            {
                Hooks.Add(hook);
                Hooks.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }
        }

        public static void Unregister(IAtoOptimizeHook hook)
        {
            if (hook == null) return;
            Hooks.Remove(hook);
        }

        public static IReadOnlyList<IAtoOptimizeHook> GetHooks() => Hooks;
    }
}
