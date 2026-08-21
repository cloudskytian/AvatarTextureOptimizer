using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato
{
    // ============================================================================
    // 扩展接口 / Extension points.
    // 为高级用户与第三方开发者预留的功能接口 / Public extension points for advanced
    // users and third-party developers. Register implementations via ATOExtensionRegistry.
    // ============================================================================

    /// <summary>
    /// 预处理钩子: 在收集阶段之前运行 / Pre-processing hook: runs before collection.
    /// </summary>
    public interface IATOPreProcessor
    {
        /// <summary>在 ATO 全部处理开始前调用 / Called before any ATO processing starts.</summary>
        void OnPreProcess(ATOPipelineContext context);
    }

    /// <summary>
    /// 后处理钩子: 在所有图集/网格应用完成后、组件移除前调用 / Post-processing hook: called after
    /// atlases/meshes are applied but before the ATO component is removed.
    /// </summary>
    public interface IATOPostProcessor
    {
        void OnPostProcess(ATOPipelineContext context);
    }

    /// <summary>
    /// 自定义贴图类别解析: 允许第三方为自定义着色器提供贴图类别与 UV 通道信息.
    /// Custom texture-category resolver: lets third parties supply texture categories
    /// and UV-channel usage for custom shaders.
    /// </summary>
    public interface IATOTextureCategoryResolver
    {
        /// <summary>shader 名称 / The shader name to match (e.g. "lilToon").</summary>
        string ShaderName { get; }

        /// <summary>
        /// 返回贴图属性名到类别与UV通道的映射; 返回 null 表示不处理该着色器.
        /// Returns a mapping from texture property name to (category, uvChannel); null = not handled.
        /// </summary>
        Dictionary<string, (ATOTextureCategory category, int uvChannel)> Resolve(Shader shader);
    }

    /// <summary>扩展注册表 / Extension registry.</summary>
    public static class ATOExtensionRegistry
    {
        private static readonly List<IATOPreProcessor> PreProcessors = new List<IATOPreProcessor>();
        private static readonly List<IATOPostProcessor> PostProcessors = new List<IATOPostProcessor>();
        private static readonly List<IATOTextureCategoryResolver> CategoryResolvers = new List<IATOTextureCategoryResolver>();

        public static void Register(IATOPreProcessor p)
        {
            if (!PreProcessors.Contains(p)) PreProcessors.Add(p);
        }

        public static void Register(IATOPostProcessor p)
        {
            if (!PostProcessors.Contains(p)) PostProcessors.Add(p);
        }

        public static void Register(IATOTextureCategoryResolver r)
        {
            if (!CategoryResolvers.Contains(r)) CategoryResolvers.Add(r);
        }

        public static void UnregisterAll(object owner)
        {
            // 简单起见只提供清空 / keep it simple: only full clear is exposed
        }

        public static IReadOnlyList<IATOPreProcessor> GetPreProcessors() => PreProcessors;
        public static IReadOnlyList<IATOPostProcessor> GetPostProcessors() => PostProcessors;
        public static IReadOnlyList<IATOTextureCategoryResolver> GetCategoryResolvers() => CategoryResolvers;
    }

    /// <summary>
    /// 流水线上下文: 暴露给扩展的构建信息快照 / Pipeline context snapshot exposed to extensions.
    /// </summary>
    public sealed class ATOPipelineContext
    {
        /// <summary>Avatar 根对象 / The avatar root GameObject.</summary>
        public GameObject AvatarRoot { get; internal set; }

        /// <summary>用户配置 / The user's configuration component.</summary>
        public AvatarTextureOptimizer Settings { get; internal set; }

        /// <summary>已收集贴图信息 / All collected texture info.</summary>
        public IReadOnlyList<ATOTextureInfo> Textures { get; internal set; }

        /// <summary>生成的图集 / Generated atlases.</summary>
        public IReadOnlyList<ATOAtlas> Atlases { get; internal set; }
    }
}
