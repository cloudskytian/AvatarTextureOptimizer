// ATO Public API - Extension interfaces for third-party developers
// ATO公共API - 第三方开发者扩展接口

using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.API
{
    /// <summary>
    /// Interface for extending ATO's shader analysis capabilities.
    /// 用于扩展ATO着色器分析功能的接口。
    /// 
    /// Implement this interface to add support for custom shaders.
    /// 实现此接口以添加对自定义着色器的支持。
    /// 
    /// Register your implementation via:
    /// 通过以下方式注册实现：
    /// [InitializeOnLoad] static class MyRegistrar { static MyRegistrar() { ATOShaderAnalyzerRegistry.Register(new MyAnalyzer()); } }
    /// </summary>
    public interface IATOShaderAnalyzer
    {
        /// <summary>
        /// Returns true if this analyzer can handle the given shader.
        /// 如果此分析器能处理给定着色器则返回true。
        /// </summary>
        bool CanAnalyze(Shader shader);

        /// <summary>
        /// Analyze the material and return texture properties.
        /// 分析材质并返回贴图属性。
        /// </summary>
        List<ShaderTexturePropertyInfo> GetTextureProperties(Material material);

        /// <summary>
        /// Get the transparency mode for this material.
        /// 获取此材质的透明模式。
        /// </summary>
        TransparencyModeInfo GetTransparencyMode(Material material);

        /// <summary>
        /// Check if a texture property has ST transforms that prevent optimization.
        /// 检查贴图属性是否具有阻止优化的ST变换。
        /// </summary>
        bool HasSTTransform(Material material, string propertyName);
    }

    /// <summary>
    /// Information about a texture property in a shader.
    /// 着色器中贴图属性的信息。
    /// </summary>
    public class ShaderTexturePropertyInfo
    {
        public string PropertyName { get; set; }
        public TextureRoleInfo Role { get; set; }
        public int UVChannel { get; set; } = 0;
        public bool IsDecalOrSpecial { get; set; }
    }

    public enum TextureRoleInfo
    {
        MainColor,
        NormalMap,
        Mask,
        Emission,
        Occlusion,
        Metallic,
        Roughness,
        AlphaMask,
        Detail,
        Other
    }

    public enum TransparencyModeInfo
    {
        Opaque,
        Cutout,
        Blend,
        Premultiply,
        Additive
    }

    /// <summary>
    /// Registry for custom shader analyzers.
    /// 自定义着色器分析器的注册表。
    /// </summary>
    public static class ATOShaderAnalyzerRegistry
    {
        private static readonly List<IATOShaderAnalyzer> _analyzers = new List<IATOShaderAnalyzer>();

        public static void Register(IATOShaderAnalyzer analyzer)
        {
            if (analyzer != null && !_analyzers.Contains(analyzer))
                _analyzers.Add(analyzer);
        }

        public static IReadOnlyList<IATOShaderAnalyzer> GetAnalyzers() => _analyzers;

        public static IATOShaderAnalyzer FindAnalyzer(Shader shader)
        {
            foreach (var analyzer in _analyzers)
            {
                if (analyzer.CanAnalyze(shader))
                    return analyzer;
            }
            return null;
        }
    }

    /// <summary>
    /// Interface for extending ATO's texture processing pipeline.
    /// 用于扩展ATO贴图处理管线的接口。
    /// </summary>
    public interface IATOTextureProcessor
    {
        /// <summary>
        /// Called before atlas generation. Can modify island data.
        /// 在图集生成之前调用。可以修改岛数据。
        /// </summary>
        void PreProcess(ATOProcessingContext context);

        /// <summary>
        /// Called after atlas generation. Can modify atlas results.
        /// 在图集生成之后调用。可以修改图集结果。
        /// </summary>
        void PostProcess(ATOProcessingContext context);
    }

    /// <summary>
    /// Context passed to custom texture processors.
    /// 传递给自定义贴图处理器的上下文。
    /// </summary>
    public class ATOProcessingContext
    {
        public GameObject AvatarRoot { get; set; }
        public List<IslandInfo> Islands { get; set; }
        public List<AtlasInfo> Atlases { get; set; }
        public TargetPlatformInfo Platform { get; set; }
    }

    public class IslandInfo
    {
        public int Id { get; set; }
        public float UVArea { get; set; }
        public float PhysicalArea { get; set; }
        public Vector2 ScaleFactor { get; set; }
        public bool IsWhitelisted { get; set; }
    }

    public class AtlasInfo
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public float Utilization { get; set; }
        public int IslandCount { get; set; }
    }

    public enum TargetPlatformInfo
    {
        PC,
        Android,
        iOS
    }

    /// <summary>
    /// Registry for custom texture processors.
    /// 自定义贴图处理器的注册表。
    /// </summary>
    public static class ATOTextureProcessorRegistry
    {
        private static readonly List<IATOTextureProcessor> _processors = new List<IATOTextureProcessor>();

        public static void Register(IATOTextureProcessor processor)
        {
            if (processor != null && !_processors.Contains(processor))
                _processors.Add(processor);
        }

        public static IReadOnlyList<IATOTextureProcessor> GetProcessors() => _processors;
    }
}
