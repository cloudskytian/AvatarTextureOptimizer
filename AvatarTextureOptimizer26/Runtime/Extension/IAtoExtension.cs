using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Third-party / advanced-user extension hook.
    /// 第三方与高级用户扩展接口。
    /// Register via <see cref="AtoExtensionRegistry.Register"/>.
    /// </summary>
    public interface IAtoExtension
    {
        /// <summary>Unique id, e.g. "vendor.tool". / 唯一 id。</summary>
        string Id { get; }

        /// <summary>
        /// Return false to force-whitelist a texture. / 返回 false 则强制白名单该贴图。
        /// </summary>
        bool ShouldProcessTexture(Texture2D texture, Material material, string propertyName);

        /// <summary>
        /// Optional override of texture kind. Return Unknown to keep analyzer result.
        /// 可选覆盖贴图类型。返回 Unknown 表示沿用分析器结果。
        /// </summary>
        AtoTextureKind ClassifyTexture(Texture2D texture, Material material, string propertyName);

        /// <summary>
        /// Called after atlases are generated, before component removal.
        /// 图集生成后、移除组件前调用。
        /// </summary>
        void OnAfterOptimize(GameObject avatarRoot, IReadOnlyList<Texture2D> generatedAtlases);
    }

    /// <summary>
    /// Global extension registry. / 全局扩展注册表。
    /// </summary>
    public static class AtoExtensionRegistry
    {
        private static readonly List<IAtoExtension> Extensions = new List<IAtoExtension>();

        public static void Register(IAtoExtension extension)
        {
            if (extension == null) throw new ArgumentNullException(nameof(extension));
            if (!Extensions.Contains(extension)) Extensions.Add(extension);
        }

        public static void Unregister(IAtoExtension extension)
        {
            if (extension != null) Extensions.Remove(extension);
        }

        public static IReadOnlyList<IAtoExtension> All => Extensions;
    }
}
