// ATOExtensions.cs — 扩展接口（高级用户与第三方开发者）/ Extension APIs (for advanced users & third-party developers).
// 说明：为各功能预留接口：自定义着色器贴图用途分析、岛后处理、图集后处理、质量评估替换。
// 实现方式：在任意程序集实现接口并用 [ATOExtension] 标记（或直接调用 ATOExtensionRegistry.Register）。
// Note: reserved extension points: custom shader texture-usage analysis, island post-processing, atlas
// post-processing, and quality-evaluator replacement. Implement the interfaces in any assembly and mark them
// with [ATOExtension] (or call ATOExtensionRegistry.Register directly).

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>扩展标记特性。/ Extension marker attribute.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class ATOExtensionAttribute : Attribute
    {
        /// <summary>优先级（小者先执行）。/ Priority (lower runs first).</summary>
        public int Priority { get; }
        public ATOExtensionAttribute(int priority = 100) { Priority = priority; }
    }

    /// <summary>
    /// 自定义着色器贴图用途提供者：为未知着色器补充贴图用途分析（角色/UV 通道/ST）。
    /// Custom shader texture-usage provider: supplements texture usage analysis (role/UV channel/ST) for unknown shaders.
    /// </summary>
    public interface IATOTextureUsageProvider
    {
        /// <summary>能处理的着色器名（含子串即可）。/ Shader names this provider can handle (substring match).</summary>
        string ShaderNameMatch { get; }

        /// <summary>分析材质并返回贴图用途（无法确定的用途应标记 whitelisted）。/ Analyze a material; uncertain usages must be marked whitelisted.</summary>
        List<ATOTextureUsage> Analyze(Material material);
    }

    /// <summary>
    /// 岛后处理器：在质量求解后、装箱前对岛引用做自定义处理（如强制某贴图整图路径）。
    /// Island post-processor: runs after quality solving and before packing (e.g. force whole-texture path).
    /// </summary>
    public interface IATOIslandPostProcessor
    {
        void Process(List<ATOIsland> islands);
    }

    /// <summary>
    /// 图集后处理器：在图集合成后、写入 PNG 前对图集像素做自定义处理。
    /// Atlas post-processor: runs after composition and before PNG writing.
    /// </summary>
    public interface IATOAtlasPostProcessor
    {
        void Process(ATOBin bin, ATORole role, Unity.Collections.NativeArray<Unity.Mathematics.float4> pixels, int width, int height);
    }

    /// <summary>扩展注册表。/ Extension registry.</summary>
    public static class ATOExtensionRegistry
    {
        private static readonly List<IATOTextureUsageProvider> UsageProviders = new List<IATOTextureUsageProvider>();
        private static readonly List<IATOIslandPostProcessor> IslandProcessors = new List<IATOIslandPostProcessor>();
        private static readonly List<IATOAtlasPostProcessor> AtlasProcessors = new List<IATOAtlasPostProcessor>();
        private static bool _scanned;

        /// <summary>注册贴图用途提供者。/ Register a texture usage provider.</summary>
        public static void Register(IATOTextureUsageProvider provider)
        {
            if (provider != null && !UsageProviders.Contains(provider)) UsageProviders.Add(provider);
        }

        /// <summary>注册岛后处理器。/ Register an island post-processor.</summary>
        public static void Register(IATOIslandPostProcessor processor)
        {
            if (processor != null && !IslandProcessors.Contains(processor)) IslandProcessors.Add(processor);
        }

        /// <summary>注册图集后处理器。/ Register an atlas post-processor.</summary>
        public static void Register(IATOAtlasPostProcessor processor)
        {
            if (processor != null && !AtlasProcessors.Contains(processor)) AtlasProcessors.Add(processor);
        }

        /// <summary>全部贴图用途提供者（含自动扫描）。/ All texture usage providers (incl. auto-scanned).</summary>
        public static IReadOnlyList<IATOTextureUsageProvider> GetUsageProviders()
        {
            EnsureScanned();
            return UsageProviders;
        }

        public static IReadOnlyList<IATOIslandPostProcessor> GetIslandProcessors()
        {
            EnsureScanned();
            return IslandProcessors;
        }

        public static IReadOnlyList<IATOAtlasPostProcessor> GetAtlasProcessors()
        {
            EnsureScanned();
            return AtlasProcessors;
        }

        /// <summary>反射扫描全部程序集，注册带 [ATOExtension] 的实现。/ Scan all assemblies for [ATOExtension] implementations.</summary>
        private static void EnsureScanned()
        {
            if (_scanned) return;
            _scanned = true;
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException) { continue; }
                    foreach (var type in types)
                    {
                        if (type.IsAbstract || type.IsInterface) continue;
                        var attr = type.GetCustomAttribute<ATOExtensionAttribute>();
                        if (attr == null) continue;
                        try
                        {
                            if (typeof(IATOTextureUsageProvider).IsAssignableFrom(type))
                                Register((IATOTextureUsageProvider)Activator.CreateInstance(type));
                            if (typeof(IATOIslandPostProcessor).IsAssignableFrom(type))
                                Register((IATOIslandPostProcessor)Activator.CreateInstance(type));
                            if (typeof(IATOAtlasPostProcessor).IsAssignableFrom(type))
                                Register((IATOAtlasPostProcessor)Activator.CreateInstance(type));
                        }
                        catch (Exception e)
                        {
                            ATOLog.Warning($"Failed to instantiate ATO extension {type.FullName}: {e.Message}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ATOLog.Verbose($"ATO extension scan failed: {e.Message}");
            }
        }
    }
}
