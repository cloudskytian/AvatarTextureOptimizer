using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Extension point: custom texture usage classification. Called AFTER the built-in analysis;
    /// providers may refine the classification or force whitelisting (Kind = Unknown). /
    /// 扩展点：自定义贴图用法分类。在内置分析之后调用；可细化分类或强制白名单（Kind = Unknown）。
    /// </summary>
    public abstract class AtoTextureUsageProvider
    {
        /// <summary>Provider name (shown in logs). / 提供者名称（日志显示）。</summary>
        public abstract string DisplayName { get; }

        /// <summary>
        /// Refine a built-in classification. Return the (possibly modified) usage, or null to keep
        /// the built-in result. / 细化内置分类。返回（可能修改过的）用法；返回 null 表示沿用内置结果。
        /// </summary>
        public abstract AtoTextureUsage Override(Material material, string propertyName, Texture2D texture,
            AtoTextureUsage builtIn);
    }

    /// <summary>
    /// Extension point: custom quality metrics. Evaluated after all built-in metrics pass; any
    /// provider returning false fails the candidate scale. / 扩展点：自定义质量指标。在内置指标全部通过后
    /// 评估；任一提供者返回 false 则判定该缩放不达标。
    /// </summary>
    public abstract class AtoQualityMetricProvider
    {
        /// <summary>Provider name (shown in logs). / 提供者名称（日志显示）。</summary>
        public abstract string DisplayName { get; }

        /// <summary>
        /// Evaluate the candidate against the reference. / 比较候选与参考。
        /// </summary>
        /// <param name="context">Reference/candidate pixels and mask (linear-premultiplied float4 + raw). /
        /// 参考/候选像素与掩码（线性预乘 float4 与原始）。</param>
        /// <returns>True when the candidate passes this metric. / 候选通过该指标时返回 true。</returns>
        public abstract bool Evaluate(AtoCustomMetricContext context);
    }

    /// <summary>
    /// Custom metric context: reference & candidate data at native island resolution. /
    /// 自定义指标上下文：原生岛分辨率下的参考与候选数据。
    /// </summary>
    public sealed class AtoCustomMetricContext
    {
        /// <summary>Island mask (1 = inside the island). / 岛掩码（1=岛内）。</summary>
        public byte[] Mask;

        /// <summary>Linear premultiplied reference. / 线性预乘参考。</summary>
        public Unity.Mathematics.float4[] Reference;

        /// <summary>Linear premultiplied candidate (upsampled back to native resolution). /
        /// 线性预乘候选（已上采样回原生分辨率）。</summary>
        public Unity.Mathematics.float4[] Candidate;

        /// <summary>Raw source pixels (stored color space). / 原始来源像素（存储色彩空间）。</summary>
        public Color32[] RawPixels;

        public int Width;
        public int Height;
    }

    /// <summary>
    /// Extension registry: auto-discovers provider implementations and allows manual registration. /
    /// 扩展注册表：自动发现提供者实现，也支持手动注册。
    /// </summary>
    public static class AtoExtensionRegistry
    {
        private static List<AtoTextureUsageProvider> _usageProviders;
        private static List<AtoQualityMetricProvider> _metricProviders;
        private static bool _scanned;

        /// <summary>All registered texture usage providers. / 全部已注册的贴图用法提供者。</summary>
        public static IReadOnlyList<AtoTextureUsageProvider> TextureUsageProviders
        {
            get
            {
                Scan();
                return _usageProviders;
            }
        }

        /// <summary>All registered quality metric providers. / 全部已注册的质量指标提供者。</summary>
        public static IReadOnlyList<AtoQualityMetricProvider> QualityMetricProviders
        {
            get
            {
                Scan();
                return _metricProviders;
            }
        }

        public static void Register(AtoTextureUsageProvider provider)
        {
            Scan();
            if (provider != null) _usageProviders.Add(provider);
        }

        public static void Register(AtoQualityMetricProvider provider)
        {
            Scan();
            if (provider != null) _metricProviders.Add(provider);
        }

        private static void Scan()
        {
            if (_scanned) return;
            _scanned = true;
            _usageProviders = new List<AtoTextureUsageProvider>();
            _metricProviders = new List<AtoQualityMetricProvider>();

            // Auto-discover provider types in loaded assemblies. / 自动发现已加载程序集中的提供者类型。
            try
            {
                var types = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic)
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); }
                        catch (Exception) { return Type.EmptyTypes; }
                    });

                foreach (var type in types)
                {
                    if (type.IsAbstract || type.IsInterface) continue;
                    try
                    {
                        if (typeof(AtoTextureUsageProvider).IsAssignableFrom(type))
                        {
                            _usageProviders.Add((AtoTextureUsageProvider)Activator.CreateInstance(type));
                        }
                        else if (typeof(AtoQualityMetricProvider).IsAssignableFrom(type))
                        {
                            _metricProviders.Add((AtoQualityMetricProvider)Activator.CreateInstance(type));
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[ATO] failed to instantiate extension provider {type.FullName}: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ATO] extension discovery failed: {e.Message}");
            }
        }
    }
}
