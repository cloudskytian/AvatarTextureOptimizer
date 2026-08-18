// ExtensionAPI.cs / ExtensionAPI.cs
// Public extension points for third-party developers to add custom shader recognizers,
// custom quality metrics, custom post-processors, etc.
// 为第三方开发者提供的公开扩展点：自定义着色器识别器、自定义质量指标、自定义后处理器等。

using System;
using System.Collections.Generic;
using net.fosa.avatar_texture_optimizer.Editor.Atlas;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor
{
    /// <summary>
    /// Delegate type for custom shader texture property recognizers. Given a Material and its Shader,
    /// return an enumerable of (propName, kind, uvChannel) tuples for each texture property that should
    /// be considered by ATO.
    /// 自定义着色器贴图属性识别器委托类型。给定Material和Shader，返回应被ATO考虑的每个贴图属性
    /// 的 (propName, kind, uvChannel) 元组。
    /// </summary>
    public delegate IEnumerable<(string propName, TexturePropertyKind kind, int uvChannel, AlphaMode alphaMode, float cutoff)> ShaderTextureRecognizer(Material mat);

    /// <summary>
    /// Delegate for custom post-processing passes that run after atlas/texture generation.
    /// 自定义后处理Pass委托：在图集/贴图生成之后运行。
    /// </summary>
    public delegate void CustomPostProcessor(AvatarAnalysisResult analysis, List<AtlasTexture> atlases);

    /// <summary>
    /// Public registration API for third-party extensions.
    /// 面向第三方扩展的公开注册API。
    /// </summary>
    public static class ATOExtensions
    {
        private static readonly List<ShaderTextureRecognizer> _recognizers = new List<ShaderTextureRecognizer>();
        private static readonly List<CustomPostProcessor> _postProcessors = new List<CustomPostProcessor>();

        /// <summary>Register a custom shader texture recognizer. / 注册自定义着色器贴图识别器。</summary>
        public static void RegisterShaderRecognizer(ShaderTextureRecognizer recognizer)
        {
            if (recognizer != null && !_recognizers.Contains(recognizer))
                _recognizers.Add(recognizer);
        }

        /// <summary>Register a custom post-processor that runs after atlases are built. / 注册图集构建后运行的自定义后处理器。</summary>
        public static void RegisterPostProcessor(CustomPostProcessor processor)
        {
            if (processor != null && !_postProcessors.Contains(processor))
                _postProcessors.Add(processor);
        }

        internal static IEnumerable<ShaderTextureRecognizer> GetRecognizers() => _recognizers;
        internal static IEnumerable<CustomPostProcessor> GetPostProcessors() => _postProcessors;
    }
}
