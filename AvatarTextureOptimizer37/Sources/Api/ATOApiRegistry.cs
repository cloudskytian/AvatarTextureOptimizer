// ============================================================================
// ATO public API - extension registry
// ATO 公开 API - 扩展注册表
//
// Third parties register their extensions once (e.g. in an
// [InitializeOnLoad] static constructor). All registrations are auto-
// discovered: every public, parameterless, non-abstract class implementing an
// ATO extension interface is instantiated automatically at plugin configure
// time; explicit Register() calls are queried first (registration order).
// 第三方一次性注册扩展（例如在 [InitializeOnLoad] 静态构造器中）。所有注册均自
// 动发现：实现 ATO 扩展接口的每个公开、无参、非抽象类都会在插件配置时自动实
// 例化；显式 Register() 的实例优先（按注册顺序）被查询。
// ============================================================================

#region

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Api
{
    /// <summary>Registry for all ATO extension points.
    /// ATO 全部扩展点注册表。</summary>
    public static class ATOApiRegistry
    {
        private static readonly List<IATOShaderAnalyzer> _shaderAnalyzers = new();
        private static readonly List<IATOWhitelistContributor> _whitelistContributors = new();
        private static readonly List<IATOQualityMetric> _qualityMetrics = new();
        private static readonly List<IATOAtlasPacker> _atlasPacker = new();
        private static readonly List<IATOTexturePostProcessor> _postProcessors = new();

        private static bool _autoDiscovered;

        /// <summary>Shader analyzers in query order. 着色器分析器（查询顺序）。</summary>
        public static IReadOnlyList<IATOShaderAnalyzer> ShaderAnalyzers => _shaderAnalyzers;
        /// <summary>Whitelist contributors. 白名单贡献者。</summary>
        public static IReadOnlyList<IATOWhitelistContributor> WhitelistContributors => _whitelistContributors;
        /// <summary>Custom quality metrics. 自定义质量指标。</summary>
        public static IReadOnlyList<IATOQualityMetric> QualityMetrics => _qualityMetrics;
        /// <summary>The active atlas packer (last registered wins).
        /// 生效的装箱器（后注册者生效）。</summary>
        public static IATOAtlasPacker AtlasPacker => _atlasPacker.Count > 0 ? _atlasPacker[_atlasPacker.Count - 1] : null;
        /// <summary>Texture post processors. 贴图后处理器。</summary>
        public static IReadOnlyList<IATOTexturePostProcessor> PostProcessors => _postProcessors;

        // ------------------------------------------------------------------
        // Explicit registration 显式注册
        // ------------------------------------------------------------------
        public static void Register(IATOShaderAnalyzer analyzer)
        {
            if (analyzer != null) _shaderAnalyzers.Add(analyzer);
        }

        public static void Register(IATOWhitelistContributor contributor)
        {
            if (contributor != null) _whitelistContributors.Add(contributor);
        }

        public static void Register(IATOQualityMetric metric)
        {
            if (metric != null) _qualityMetrics.Add(metric);
        }

        public static void Register(IATOAtlasPacker packer)
        {
            if (packer != null) _atlasPacker.Add(packer);
        }

        public static void Register(IATOTexturePostProcessor processor)
        {
            if (processor != null) _postProcessors.Add(processor);
        }

        /// <summary>Clears all registrations (mainly for tests).
        /// 清空全部注册（主要用于测试）。</summary>
        public static void Reset()
        {
            _shaderAnalyzers.Clear();
            _whitelistContributors.Clear();
            _qualityMetrics.Clear();
            _atlasPacker.Clear();
            _postProcessors.Clear();
            _autoDiscovered = false;
        }

        /// <summary>Auto-discovers extension implementations in loaded
        /// assemblies. Called by the ATO plugin at configure time.
        /// 自动发现已加载程序集中的扩展实现。由 ATO 插件在配置时调用。</summary>
        public static void AutoDiscover()
        {
            if (_autoDiscovered) return;
            _autoDiscovered = true;

            var interfaces = new[]
            {
                typeof(IATOShaderAnalyzer),
                typeof(IATOWhitelistContributor),
                typeof(IATOQualityMetric),
                typeof(IATOAtlasPacker),
                typeof(IATOTexturePostProcessor),
            };

            Assembly[] assemblies;
            try
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch (ReflectionTypeLoadException)
            {
                return; // nothing we can do in exotic environments
            }
            catch (Exception)
            {
                return;
            }

            foreach (var iface in interfaces)
            {
                foreach (var asm in assemblies)
                {
                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types.Where(t => t != null).ToArray();
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (var type in types)
                    {
                        if (type == null || type.IsAbstract || type.IsInterface) continue;
                        if (!iface.IsAssignableFrom(type)) continue;
                        if (!type.IsPublic) continue;
                        var ctor = type.GetConstructor(Type.EmptyTypes);
                        if (ctor == null) continue;
                        try
                        {
                            var instance = (object) ctor.Invoke(null);
                            if (iface == typeof(IATOShaderAnalyzer))
                                _shaderAnalyzers.Add((IATOShaderAnalyzer) instance);
                            else if (iface == typeof(IATOWhitelistContributor))
                                _whitelistContributors.Add((IATOWhitelistContributor) instance);
                            else if (iface == typeof(IATOQualityMetric))
                                _qualityMetrics.Add((IATOQualityMetric) instance);
                            else if (iface == typeof(IATOAtlasPacker))
                                _atlasPacker.Add((IATOAtlasPacker) instance);
                            else if (iface == typeof(IATOTexturePostProcessor))
                                _postProcessors.Add((IATOTexturePostProcessor) instance);
                        }
                        catch (Exception)
                        {
                            // A broken third-party extension must not break ATO.
                            // 第三方扩展出错不应破坏 ATO。
                        }
                    }
                }
            }
        }
    }
}
