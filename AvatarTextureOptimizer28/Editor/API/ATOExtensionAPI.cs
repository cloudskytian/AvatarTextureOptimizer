using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor.api
{
    /// <summary>
    /// EN: Lets a third party declare that a texture property of a shader they own is (or is not) a
    ///     plain mesh-UV lookup. Registered providers are consulted before ATO's built-in heuristics, so
    ///     a shader author can opt their shader into atlasing without patching ATO.
    /// ZH: 让第三方声明自己着色器的某个贴图属性是（或不是）普通的网格 UV 查表。
    ///     已注册的提供者会在 ATO 的内置启发式之前被咨询，
    ///     因此着色器作者无需修改 ATO 即可让自己的着色器加入图集化。
    /// </summary>
    public interface IShaderSupportProvider
    {
        /// <summary>EN: True when this provider knows the shader. ZH: 该提供者是否认识这个着色器。</summary>
        bool Handles(Shader shader);

        /// <summary>
        /// EN: Describe a texture property. Return false to fall back to the built-in analysis.
        /// ZH: 描述一个贴图属性。返回 false 表示回退到内置分析。
        /// </summary>
        /// <param name="material">EN: the material. ZH: 材质。</param>
        /// <param name="property">EN: shader property name. ZH: 着色器属性名。</param>
        /// <param name="safe">EN: out - safe to atlas. ZH: 输出 - 可安全图集化。</param>
        /// <param name="uvChannel">EN: out - UV channel sampled. ZH: 输出 - 采样所用 UV 通道。</param>
        /// <param name="slot">EN: out - semantic slot. ZH: 输出 - 语义槽位。</param>
        bool Describe(Material material, string property, out bool safe, out int uvChannel, out TextureSlot slot);
    }

    /// <summary>
    /// EN: Observes the build so external tooling can react to what ATO produced.
    /// ZH: 观察构建过程，使外部工具可以对 ATO 的产出作出反应。
    /// </summary>
    public interface IATOBuildObserver
    {
        /// <summary>EN: Called after UV groups have been built. ZH: UV 组构建完成后调用。</summary>
        void OnGroupsBuilt(IReadOnlyList<UVGroup> groups);

        /// <summary>EN: Called after every atlas has been baked. ZH: 所有图集烘焙完成后调用。</summary>
        void OnAtlasesBaked(IReadOnlyList<BakedAtlas> atlases);

        /// <summary>EN: Called with the final texture remapping. ZH: 最终贴图重映射完成时调用。</summary>
        void OnRemapReady(IReadOnlyDictionary<Texture2D, Texture2D> remap);
    }

    /// <summary>
    /// EN: Central registry for the extension points above. Registration is process-wide and typically
    ///     done from an [InitializeOnLoad] static constructor.
    /// ZH: 上述扩展点的集中注册表。注册是进程级的，通常在 [InitializeOnLoad] 的静态构造函数中完成。
    /// </summary>
    public static class ATOExtensionRegistry
    {
        private static readonly List<IShaderSupportProvider> _shaderProviders = new List<IShaderSupportProvider>();
        private static readonly List<IATOBuildObserver> _observers = new List<IATOBuildObserver>();

        /// <summary>EN: All registered shader providers. ZH: 所有已注册的着色器提供者。</summary>
        public static IReadOnlyList<IShaderSupportProvider> ShaderProviders => _shaderProviders;

        /// <summary>EN: All registered observers. ZH: 所有已注册的观察者。</summary>
        public static IReadOnlyList<IATOBuildObserver> Observers => _observers;

        /// <summary>EN: Register a shader support provider. ZH: 注册一个着色器支持提供者。</summary>
        public static void Register(IShaderSupportProvider provider)
        {
            if (provider != null && !_shaderProviders.Contains(provider)) _shaderProviders.Add(provider);
        }

        /// <summary>EN: Register a build observer. ZH: 注册一个构建观察者。</summary>
        public static void Register(IATOBuildObserver observer)
        {
            if (observer != null && !_observers.Contains(observer)) _observers.Add(observer);
        }

        /// <summary>EN: Remove a previously registered extension. ZH: 移除先前注册的扩展。</summary>
        public static void Unregister(object extension)
        {
            if (extension is IShaderSupportProvider p) _shaderProviders.Remove(p);
            if (extension is IATOBuildObserver o) _observers.Remove(o);
        }

        /// <summary>EN: Ask every provider about a property; the first one that handles it wins. ZH: 依次询问所有提供者；第一个处理它的胜出。</summary>
        public static bool TryDescribe(Material material, string property,
            out bool safe, out int uvChannel, out TextureSlot slot)
        {
            safe = false; uvChannel = 0; slot = TextureSlot.Other;
            if (material == null || material.shader == null) return false;
            foreach (var p in _shaderProviders)
            {
                if (!p.Handles(material.shader)) continue;
                if (p.Describe(material, property, out safe, out uvChannel, out slot)) return true;
            }
            return false;
        }

        /// <summary>EN: Notify observers, swallowing their exceptions. ZH: 通知观察者，并吞掉它们抛出的异常。</summary>
        public static void Notify(Action<IATOBuildObserver> action)
        {
            foreach (var o in _observers)
            {
                try { action(o); }
                catch (Exception e) { Debug.LogWarning($"{ATOConstants.LogPrefix} observer threw: {e.Message}"); }
            }
        }
    }
}
