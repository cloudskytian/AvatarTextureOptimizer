using System.Collections.Generic;
using Fosa.Ato.Editor.Analysis;
using Fosa.Ato.Editor.Pipeline;
using Fosa.Ato.Runtime;
using UnityEngine;

namespace Fosa.Ato.Editor.Extensibility
{
    /// <summary>
    /// Extension point for third-party shader support. Implement and register with
    /// <see cref="AtoExtensions.RegisterShaderAnalyzer"/> to teach ATO how to classify texture
    /// properties on a custom shader (which slots are color/normal/mask, which UV channel, and whether
    /// a material uses ST transforms).
    /// 第三方着色器扩展点：实现并通过 RegisterShaderAnalyzer 注册，告诉 ATO 如何分类自定义着色器的
    /// 贴图属性（主色/法线/蒙版、UV 通道、是否使用 ST 变换）。
    /// </summary>
    public interface IShaderAnalyzer
    {
        bool CanAnalyze(Shader shader);
        IEnumerable<ShaderPropertyAnalyzer.PropertyInfo> GetProperties(Shader shader);
        int GetUvChannel(Material material, string propertyName);
        bool HasStTransform(Material material, string propertyName);
    }

    /// <summary>
    /// Extension point for custom quality metrics. A metric returns whether a candidate island scale
    /// passes; all registered metrics must pass for a scale to be accepted.
    /// 自定义质量指标扩展点：返回候选缩放是否通过；所有已注册指标都通过才算达标。
    /// </summary>
    public interface IQualityMetric
    {
        string Id { get; }
        bool Evaluate(Island island, TextureUsage usage, Color[] original, int srcW, int srcH, RectInt srcBox,
            Color[] downsampled, int smallW, int smallH, TextureClassSettings settings);
    }

    /// <summary>
    /// Extension point for custom atlas packing strategies. A packer turns a list of UV groups into
    /// placements for one atlas. ATO's built-in raster BLF packer is used by default.
    /// 自定义图集装箱策略扩展点：把 UV 组列表装入一个图集。默认使用内置光栅 BLF 装箱器。
    /// </summary>
    public interface IAtlasPacker
    {
        string Id { get; }
        bool TryPack(IReadOnlyList<UvGroup> groups, int atlasWidth, int atlasHeight, int padding,
            IDictionary<Island, (RectInt rect, bool rotated)> placements);
    }

    /// <summary>
    /// Global registry for third-party extensions. Thread-safe registration is allowed at any time
    /// before a bake starts.
    /// 第三方扩展的全局注册表，可在烘焙前任意时间注册（线程安全）。
    /// </summary>
    public static class AtoExtensions
    {
        private static readonly List<IShaderAnalyzer> ShaderAnalyzers = new();
        private static readonly List<IQualityMetric> Metrics = new();
        private static readonly List<IAtlasPacker> Packers = new();

        public static void RegisterShaderAnalyzer(IShaderAnalyzer a) { lock (ShaderAnalyzers) ShaderAnalyzers.Add(a); }
        public static void RegisterMetric(IQualityMetric m) { lock (Metrics) Metrics.Add(m); }
        public static void RegisterPacker(IAtlasPacker p) { lock (Packers) Packers.Add(p); }

        public static IReadOnlyList<IShaderAnalyzer> GetShaderAnalyzers() => ShaderAnalyzers;
        public static IReadOnlyList<IQualityMetric> GetMetrics() => Metrics;
        public static IReadOnlyList<IAtlasPacker> GetPackers() => Packers;
    }
}
