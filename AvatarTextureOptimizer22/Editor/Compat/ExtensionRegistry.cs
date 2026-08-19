// AvatarTextureOptimizer
// File: Editor/Compat/ExtensionRegistry.cs
//
// Extension points for advanced users and third-party developers. Each
// interface describes a stage of the pipeline; custom implementations are
// discovered via [InitializeOnLoad] registration and can override or augment
// the default behavior. All extensions are optional.
//
// 面向高级用户与第三方开发者的扩展点。每个接口描述流水线的一个阶段；
// 自定义实现通过 [InitializeOnLoad] 注册被发现的，可覆盖或增强默认行为。
// 所有扩展都是可选的。

using System;
using System.Collections.Generic;
using net.fosa.avatar_texture_optimizer.editor.model;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.compat
{
    /// <summary>
    /// Allows a third party to veto the optimization of individual textures
    /// (e.g. decal textures used by custom shaders). Return false to keep a
    /// texture fully untouched (it is treated like a whitelisted texture).
    /// 允许第三方否决对个别贴图的优化（例如自定义着色器使用的贴花贴图）。
    /// 返回 false 将保持贴图完全不动（视作白名单贴图）。
    /// </summary>
    public interface IATOTextureFilter
    {
        bool CanOptimize(Texture2D texture);
    }

    /// <summary>
    /// Allows a third party to veto whole UV spaces (renderer+slot+channel).
    /// 允许第三方否决整个 UV 空间（渲染器+槽+通道）。
    /// </summary>
    public interface IATOUVSpaceFilter
    {
        bool CanOptimize(UVSpaceKey space);
    }

    /// <summary>
    /// Hook called after the analysis phase with the full collected state.
    /// 分析阶段结束后、携带完整收集状态的钩子。
    /// </summary>
    public interface IATOAnalysisHook
    {
        void OnAnalysisComplete(ATOBuildState state);
    }

    /// <summary>
    /// Static registry collecting third-party extensions.
    /// 收集第三方扩展的静态注册表。
    /// </summary>
    public static class ExtensionRegistry
    {
        private static readonly List<IATOTextureFilter> TextureFilters = new List<IATOTextureFilter>();
        private static readonly List<IATOUVSpaceFilter> UVSpaceFilters = new List<IATOUVSpaceFilter>();
        private static readonly List<IATOAnalysisHook> AnalysisHooks = new List<IATOAnalysisHook>();
        private static bool _dirty = true;

        /// <summary>Register a texture filter. / 注册贴图过滤器。</summary>
        public static void RegisterTextureFilter(IATOTextureFilter filter)
        {
            if (filter != null && !TextureFilters.Contains(filter)) TextureFilters.Add(filter);
            _dirty = true;
        }

        /// <summary>Register a UV-space filter. / 注册 UV 空间过滤器。</summary>
        public static void RegisterUVSpaceFilter(IATOUVSpaceFilter filter)
        {
            if (filter != null && !UVSpaceFilters.Contains(filter)) UVSpaceFilters.Add(filter);
            _dirty = true;
        }

        /// <summary>Register an analysis hook. / 注册分析钩子。</summary>
        public static void RegisterAnalysisHook(IATOAnalysisHook hook)
        {
            if (hook != null && !AnalysisHooks.Contains(hook)) AnalysisHooks.Add(hook);
            _dirty = true;
        }

        public static bool CanOptimizeTexture(Texture2D texture)
        {
            EnsureLoaded();
            foreach (var f in TextureFilters)
                if (!f.CanOptimize(texture)) return false;
            return true;
        }

        public static bool CanOptimizeUVSpace(UVSpaceKey space)
        {
            EnsureLoaded();
            foreach (var f in UVSpaceFilters)
                if (!f.CanOptimize(space)) return false;
            return true;
        }

        public static void NotifyAnalysisComplete(ATOBuildState state)
        {
            EnsureLoaded();
            foreach (var h in AnalysisHooks)
            {
                try { h.OnAnalysisComplete(state); }
                catch (Exception e) { Debug.LogWarning($"[ATO] Analysis hook failed: {e}"); }
            }
        }

        private static void EnsureLoaded()
        {
            if (!_dirty) return;
            _dirty = false;
            // Discovery of IATO* implementations is done by the host project
            // via [InitializeOnLoadMethod] + Reflection; the base tool ships
            // without third-party implementations.
            // 宿主工程通过 [InitializeOnLoadMethod] + 反射发现 IATO* 实现；
            // 基础工具本身不附带第三方实现。
        }
    }
}
