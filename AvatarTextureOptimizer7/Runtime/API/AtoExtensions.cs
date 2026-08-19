using System;
using System.Collections.Generic;

namespace Fosa.AvatarTextureOptimizer.API
{
    /// <summary>
    /// Registration point for third-party analysers and hooks.
    /// Register from [InitializeOnLoad] / RuntimeInitializeOnLoadMethod.
    /// 第三方分析器与钩子的注册点。请在 InitializeOnLoad 中注册。
    /// </summary>
    public static class AtoExtensions
    {
        static readonly List<IAtoShaderAnalyzer> ShaderAnalyzers = new List<IAtoShaderAnalyzer>();
        static readonly List<IAtoQualityHook> QualityHooks = new List<IAtoQualityHook>();
        static readonly List<IAtoAtlasHook> AtlasHooks = new List<IAtoAtlasHook>();
        static readonly object Gate = new object();

        public static void RegisterShaderAnalyzer(IAtoShaderAnalyzer analyzer)
        {
            if (analyzer == null) throw new ArgumentNullException(nameof(analyzer));
            lock (Gate)
            {
                ShaderAnalyzers.RemoveAll(a => a != null && a.Id == analyzer.Id);
                ShaderAnalyzers.Add(analyzer);
                ShaderAnalyzers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }
        }

        public static void UnregisterShaderAnalyzer(string id)
        {
            lock (Gate) ShaderAnalyzers.RemoveAll(a => a != null && a.Id == id);
        }

        public static void RegisterQualityHook(IAtoQualityHook hook)
        {
            if (hook == null) throw new ArgumentNullException(nameof(hook));
            lock (Gate)
            {
                QualityHooks.RemoveAll(h => h != null && h.Id == hook.Id);
                QualityHooks.Add(hook);
            }
        }

        public static void RegisterAtlasHook(IAtoAtlasHook hook)
        {
            if (hook == null) throw new ArgumentNullException(nameof(hook));
            lock (Gate)
            {
                AtlasHooks.RemoveAll(h => h != null && h.Id == hook.Id);
                AtlasHooks.Add(hook);
            }
        }

        public static IReadOnlyList<IAtoShaderAnalyzer> GetShaderAnalyzers()
        {
            lock (Gate) return ShaderAnalyzers.ToArray();
        }

        public static IReadOnlyList<IAtoQualityHook> GetQualityHooks()
        {
            lock (Gate) return QualityHooks.ToArray();
        }

        public static IReadOnlyList<IAtoAtlasHook> GetAtlasHooks()
        {
            lock (Gate) return AtlasHooks.ToArray();
        }
    }
}
