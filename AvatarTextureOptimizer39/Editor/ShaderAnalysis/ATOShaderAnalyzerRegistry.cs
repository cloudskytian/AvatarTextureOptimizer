// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;

namespace AvatarTextureOptimizer.Editor.ShaderAnalysis
{
    /// <summary>
    /// Registry of shader analyzers + a shared analysis cache keyed by shader.
    /// 着色器分析器注册表 + 以 shader 为键的共享分析缓存。
    /// </summary>
    public static class ATOShaderAnalyzerRegistry
    {
        private static readonly List<IATOShaderAnalyzer> _analyzers = new List<IATOShaderAnalyzer>();
        private static readonly Dictionary<UnityEngine.Shader, ATOShaderInfo> _cache =
            new Dictionary<UnityEngine.Shader, ATOShaderInfo>();

        static ATOShaderAnalyzerRegistry()
        {
            // Order matters: specific analyzers first, generic fallback last.
            // 顺序重要：特定分析器在前，通用兜底在后。
            Register(new ATOLilToonShaderAnalyzer());
            Register(new ATOGenericShaderAnalyzer());
        }

        /// <summary>Register a custom analyzer (before the generic one if possible). 注册自定义分析器。</summary>
        public static void Register(IATOShaderAnalyzer analyzer)
        {
            if (!_analyzers.Contains(analyzer))
                _analyzers.Add(analyzer);
        }

        /// <summary>
        /// Analyze a shader, using the cache. Never returns null.
        /// 分析着色器（带缓存）。永不返回 null。
        /// </summary>
        public static ATOShaderInfo Analyze(UnityEngine.Shader shader)
        {
            if (_cache.TryGetValue(shader, out var cached)) return cached;

            var info = new ATOShaderInfo { Shader = shader };
            foreach (var a in _analyzers)
            {
                try
                {
                    if (a.TryAnalyze(shader, info))
                        break;
                }
                catch (System.Exception e)
                {
                    ATOLog.Warning($"Shader analyzer {a.GetType().Name} threw for {shader?.name}: {e.Message}");
                }
            }

            _cache[shader] = info;
            return info;
        }

        /// <summary>Clear the cache (e.g. after domain reload). 清空缓存。</summary>
        public static void ClearCache() => _cache.Clear();
    }
}
