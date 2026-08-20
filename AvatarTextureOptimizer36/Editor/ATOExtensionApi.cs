using System;
using System.Collections.Generic;
using UnityEngine;
using Fosa.AvatarTextureOptimizer;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Public texture property description for third-party shader integrations. / 面向第三方 Shader 集成的公开纹理属性描述。
    /// </summary>
    public sealed class ATOTexturePropertyDescriptor
    {
        public string PropertyName;
        public ATOTextureCategory Category;
        public int UVChannel;
        public bool IsPrimary;
    }

    /// <summary>
    /// Optional shader resolver extension. / 可选 Shader 解析扩展。
    /// </summary>
    public interface IATOShaderResolverExtension
    {
        bool TryResolve(Material material, IList<ATOTexturePropertyDescriptor> properties);
    }

    /// <summary>
    /// Build-stage extension hooks. / 构建阶段扩展钩子。
    /// </summary>
    public interface IATOBuildExtension
    {
        void BeforeAnalyze(ATOExtensionContext context);
        void AfterBuild(ATOExtensionContext context, ATOExtensionSummary summary);
    }

    /// <summary>
    /// Read-only public build context. / 公开的只读构建上下文。
    /// </summary>
    public sealed class ATOExtensionContext
    {
        public readonly nadena.dev.ndmf.BuildContext NDMFContext;
        public readonly GameObject AvatarRoot;
        public readonly AvatarTextureOptimizer Component;
        public readonly ATOPlatform Platform;

        internal ATOExtensionContext(nadena.dev.ndmf.BuildContext ndmfContext, GameObject avatarRoot,
            AvatarTextureOptimizer component, ATOPlatform platform)
        {
            NDMFContext = ndmfContext;
            AvatarRoot = avatarRoot;
            Component = component;
            Platform = platform;
        }
    }

    /// <summary>
    /// Public summary passed to extensions. / 传给扩展的公开摘要。
    /// </summary>
    public sealed class ATOExtensionSummary
    {
        public readonly int RendererCount;
        public readonly int TextureCount;
        public readonly int IslandCount;
        public readonly int AtlasCount;
        public readonly int WarningCount;

        internal ATOExtensionSummary(ATOBuildReport report)
        {
            RendererCount = report.RendererCount;
            TextureCount = report.TextureCount;
            IslandCount = report.IslandCount;
            AtlasCount = report.AtlasCount;
            WarningCount = report.Warnings.Count;
        }
    }

    /// <summary>
    /// Process-local extension registry; registration is explicit and cleared on domain reload. / 进程内扩展注册表，显式注册且域重载后清空。
    /// </summary>
    public static class ATOExtensionRegistry
    {
        private static readonly List<IATOShaderResolverExtension> ShaderExtensions = new List<IATOShaderResolverExtension>();
        private static readonly List<IATOBuildExtension> BuildExtensions = new List<IATOBuildExtension>();

        public static void RegisterShaderResolver(IATOShaderResolverExtension extension)
        {
            if (extension != null && !ShaderExtensions.Contains(extension)) ShaderExtensions.Add(extension);
        }

        public static void RegisterBuildExtension(IATOBuildExtension extension)
        {
            if (extension != null && !BuildExtensions.Contains(extension)) BuildExtensions.Add(extension);
        }

        public static void UnregisterShaderResolver(IATOShaderResolverExtension extension)
        {
            if (extension != null) ShaderExtensions.Remove(extension);
        }

        public static void UnregisterBuildExtension(IATOBuildExtension extension)
        {
            if (extension != null) BuildExtensions.Remove(extension);
        }

        internal static bool TryResolveShader(Material material, List<ResolvedTextureReference> result, ATOLogger logger)
        {
            for (int i = 0; i < ShaderExtensions.Count; i++)
            {
                List<ATOTexturePropertyDescriptor> descriptors = new List<ATOTexturePropertyDescriptor>();
                try
                {
                    result.Clear();
                    if (!ShaderExtensions[i].TryResolve(material, descriptors)) continue;
                    for (int j = 0; j < descriptors.Count; j++)
                    {
                        ATOTexturePropertyDescriptor descriptor = descriptors[j];
                        if (descriptor == null || string.IsNullOrEmpty(descriptor.PropertyName)) continue;
                        Texture texture = material.GetTexture(descriptor.PropertyName);
                        if (texture == null) continue;
                        result.Add(new ResolvedTextureReference
                        {
                            PropertyName = descriptor.PropertyName,
                            Texture = texture,
                            Category = descriptor.Category,
                            UVChannel = Mathf.Clamp(descriptor.UVChannel, 0, 7),
                            IsPrimary = descriptor.IsPrimary
                        });
                    }
                    return result.Count > 0;
                }
                catch (Exception exception)
                {
                    logger.Warning("Shader extension failed and was ignored: " + exception.Message + " / Shader 扩展失败，已忽略。");
                }
            }
            return false;
        }

        internal static void BeforeAnalyze(ATOExtensionContext context, ATOLogger logger)
        {
            for (int i = 0; i < BuildExtensions.Count; i++)
            {
                try { BuildExtensions[i].BeforeAnalyze(context); }
                catch (Exception exception) { logger.Warning("Build extension BeforeAnalyze failed: " + exception.Message); }
            }
        }

        internal static void AfterBuild(ATOExtensionContext context, ATOExtensionSummary summary, ATOLogger logger)
        {
            for (int i = 0; i < BuildExtensions.Count; i++)
            {
                try { BuildExtensions[i].AfterBuild(context, summary); }
                catch (Exception exception) { logger.Warning("Build extension AfterBuild failed: " + exception.Message); }
            }
        }
    }
}
