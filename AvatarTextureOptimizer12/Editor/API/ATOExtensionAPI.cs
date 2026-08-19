// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Public extension points for advanced users and third-party developers.
// AvatarTextureOptimizer (ATO) - 面向高级用户与第三方开发者的公开扩展点。

using System.Collections.Generic;
using nadena.dev.ndmf;
using Net.Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Net.Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Net.Fosa.AvatarTextureOptimizer.Editor.Quality;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.API
{
    /// <summary>
    /// EN: Implement and register to teach ATO about a shader it cannot analyse generically. The generic
    ///     analyser already handles anything that uses the standard property table conventions; this hook
    ///     exists for shaders that do something unusual.
    /// ZH: 实现并注册本接口，可以让 ATO 理解它无法通用分析的着色器。
    ///     通用分析器已经能处理所有遵循标准属性表约定的着色器；本钩子用于处理特殊着色器。
    /// </summary>
    public interface IATOShaderProvider
    {
        /// <summary>EN: Does this provider handle the shader? ZH: 本提供者是否处理该着色器？</summary>
        bool Supports(Shader shader);

        /// <summary>
        /// EN: Return the analysed slots, or null to fall back to the generic analyser.
        /// ZH: 返回分析出的贴图槽；返回 null 则回退到通用分析器。
        /// </summary>
        List<MaterialTextureSlot> Analyse(Material material, int availableUvChannels);
    }

    /// <summary>
    /// EN: Replace the packing strategy wholesale.
    /// ZH: 整体替换装箱策略。
    /// </summary>
    public interface IATOPackingStrategy
    {
        /// <summary>
        /// EN: Place every <see cref="AtlasPacker.PackGroup"/> and emit the resulting atlases. Placement
        ///     must be written onto the shared <c>UVIsland</c> objects so that every parallel layer of a UV
        ///     group receives the identical layout.
        /// ZH: 放置每一个 <see cref="AtlasPacker.PackGroup"/> 并输出对应图集。
        ///     位置必须写到共享的 <c>UVIsland</c> 对象上，使 UV 组的每一个平行层都获得完全相同的布局。
        /// </summary>
        List<AtlasPlan> Pack(List<AtlasPacker.PackGroup> groups, List<AtlasCandidate> pool,
            int minPadding, ref int atlasCounter);
    }

    /// <summary>
    /// EN: Observe or veto every optimisation decision. Useful for tooling and QA.
    /// ZH: 观察或否决每一个优化决策。适合工具链与 QA 使用。
    /// </summary>
    public interface IATOBuildHook
    {
        /// <summary>EN: Called after the usage graph is built. ZH: 在关系图构建完成后调用。</summary>
        void OnGraphBuilt(BuildContext ctx, UsageGraph graph);

        /// <summary>EN: Return false to force a texture to be treated as whitelisted.
        ///     ZH: 返回 false 可强制把某贴图按白名单处理。</summary>
        bool ShouldOptimise(BuildContext ctx, TextureUsage usage);

        /// <summary>EN: Called after all atlases are planned but before baking. ZH: 在图集规划完成、烘焙之前调用。</summary>
        void OnAtlasesPlanned(BuildContext ctx, List<AtlasPlan> atlases);
    }

    /// <summary>
    /// EN: Registry for all extension points. Register from an <c>[InitializeOnLoadMethod]</c>.
    /// ZH: 所有扩展点的注册表。请在 <c>[InitializeOnLoadMethod]</c> 中注册。
    /// </summary>
    public static class ATOExtensionRegistry
    {
        private static readonly List<IATOShaderProvider> _shaderProviders = new List<IATOShaderProvider>();
        private static readonly List<IATOBuildHook> _hooks = new List<IATOBuildHook>();

        public static IATOPackingStrategy PackingStrategyOverride { get; set; }

        public static IReadOnlyList<IATOShaderProvider> ShaderProviders => _shaderProviders;
        public static IReadOnlyList<IATOBuildHook> Hooks => _hooks;

        public static void Register(IATOShaderProvider provider)
        {
            if (provider != null && !_shaderProviders.Contains(provider)) _shaderProviders.Add(provider);
        }

        public static void Register(IATOBuildHook hook)
        {
            if (hook != null && !_hooks.Contains(hook)) _hooks.Add(hook);
        }

        public static void Unregister(IATOShaderProvider provider) => _shaderProviders.Remove(provider);
        public static void Unregister(IATOBuildHook hook) => _hooks.Remove(hook);
    }
}
