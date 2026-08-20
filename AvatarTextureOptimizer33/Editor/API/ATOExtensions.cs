// SPDX-License-Identifier: MIT
// EN: Public extension points for advanced users and third party developers.
// ZH: 面向高级用户与第三方开发者的公开扩展点。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.API
{
    /// <summary>
    /// EN: Lets a third party teach ATO about a shader it cannot analyse generically. Adapters are asked
    ///     in registration order; the first one that claims the shader wins.
    /// ZH: 让第三方为 ATO 补充无法通用分析的着色器信息。适配器按注册顺序询问，第一个认领的生效。
    /// </summary>
    public interface IATOShaderAdapter
    {
        /// <summary>EN: True when this adapter handles the shader. ZH: 该适配器能处理此着色器时返回 true。</summary>
        bool CanHandle(Shader shader);

        /// <summary>
        /// EN: Returns the UV channel a texture property samples, or -1 when the property is not a plain
        ///     UV lookup (which makes ATO treat it as whitelisted).
        /// ZH: 返回某贴图属性采样的 UV 通道；若不是普通 UV 采样则返回 -1（ATO 会按白名单处理）。
        /// </summary>
        int GetUVChannel(Material material, string propertyName);

        /// <summary>
        /// EN: Returns true when the property is sampled without any UV transform.
        /// ZH: 当该属性的采样不含任何 UV 变换时返回 true。
        /// </summary>
        bool IsTransformFree(Material material, string propertyName);

        /// <summary>EN: Optional role override. ZH: 可选的角色覆盖。</summary>
        bool TryGetRole(Material material, string propertyName, out ATOTextureRole role);
    }

    /// <summary>
    /// EN: Called at well defined points of the pipeline; useful for tooling and diagnostics.
    /// ZH: 在管线的固定节点被调用；适合做工具化与诊断。
    /// </summary>
    public interface IATOPipelineHook
    {
        /// <summary>EN: After the avatar scan, before islands are built. ZH: 扫描完成后、构建 UV 岛之前。</summary>
        void OnScanned(GameObject avatarRoot, ATOScanResult scan);

        /// <summary>EN: After packing, before atlases are composed. ZH: 装箱完成后、合成图集之前。</summary>
        void OnPacked(GameObject avatarRoot, IReadOnlyList<ATOAtlas> atlases);

        /// <summary>EN: After everything finished. ZH: 全部完成之后。</summary>
        void OnCompleted(GameObject avatarRoot, ATOStatistics statistics);
    }

    /// <summary>
    /// EN: Global registry for the extension points above.
    /// ZH: 上述扩展点的全局注册表。
    /// </summary>
    public static class ATOExtensions
    {
        private static readonly List<IATOShaderAdapter> Adapters = new List<IATOShaderAdapter>();
        private static readonly List<IATOPipelineHook> Hooks = new List<IATOPipelineHook>();

        public static IReadOnlyList<IATOShaderAdapter> ShaderAdapters => Adapters;
        public static IReadOnlyList<IATOPipelineHook> PipelineHooks => Hooks;

        public static void RegisterShaderAdapter(IATOShaderAdapter adapter)
        {
            if (adapter != null && !Adapters.Contains(adapter)) Adapters.Add(adapter);
        }

        public static void UnregisterShaderAdapter(IATOShaderAdapter adapter) => Adapters.Remove(adapter);

        public static void RegisterHook(IATOPipelineHook hook)
        {
            if (hook != null && !Hooks.Contains(hook)) Hooks.Add(hook);
        }

        public static void UnregisterHook(IATOPipelineHook hook) => Hooks.Remove(hook);

        internal static void InvokeScanned(GameObject root, ATOScanResult scan)
        {
            foreach (var h in Hooks) SafeInvoke(() => h.OnScanned(root, scan));
        }

        internal static void InvokePacked(GameObject root, IReadOnlyList<ATOAtlas> atlases)
        {
            foreach (var h in Hooks) SafeInvoke(() => h.OnPacked(root, atlases));
        }

        internal static void InvokeCompleted(GameObject root, ATOStatistics statistics)
        {
            foreach (var h in Hooks) SafeInvoke(() => h.OnCompleted(root, statistics));
        }

        private static void SafeInvoke(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogError($"{ATOLog.Prefix}[api] extension threw: {e}");
            }
        }
    }
}
