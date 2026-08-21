using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Extension point for advanced shader semantic providers.
    /// 面向高级用户和第三方开发者的着色器语义扩展点。
    /// </summary>
    public interface IAtoShaderSemanticProvider
    {
        /// <summary>
        /// Attempts to describe a texture property's semantic role and UV usage.
        /// 尝试描述贴图属性的语义角色与 UV 用法。
        /// </summary>
        bool TryDescribe(Material material, string textureProperty, out AtoShaderSemanticDescription description);
    }

    /// <summary>
    /// Public description payload returned by semantic providers.
    /// 语义提供器返回的公开描述数据。
    /// </summary>
    public readonly struct AtoShaderSemanticDescription
    {
        public readonly AtoTextureSemantic Semantic;
        public readonly int UvChannel;
        public readonly bool RequiresIdentityTransform;
        public readonly string Notes;

        public AtoShaderSemanticDescription(AtoTextureSemantic semantic, int uvChannel, bool requiresIdentityTransform, string notes)
        {
            Semantic = semantic;
            UvChannel = uvChannel;
            RequiresIdentityTransform = requiresIdentityTransform;
            Notes = notes;
        }
    }

    /// <summary>
    /// Global registry for extension providers.
    /// 扩展提供器全局注册表。
    /// </summary>
    public static class AtoExtensionRegistry
    {
        private static readonly List<IAtoShaderSemanticProvider> ShaderSemanticProviders = new List<IAtoShaderSemanticProvider>();

        public static IReadOnlyList<IAtoShaderSemanticProvider> RegisteredShaderSemanticProviders => ShaderSemanticProviders;

        public static void Register(IAtoShaderSemanticProvider provider)
        {
            if (provider == null || ShaderSemanticProviders.Contains(provider))
            {
                return;
            }

            ShaderSemanticProviders.Add(provider);
        }

        public static void Unregister(IAtoShaderSemanticProvider provider)
        {
            if (provider == null)
            {
                return;
            }

            ShaderSemanticProviders.Remove(provider);
        }
    }
}
