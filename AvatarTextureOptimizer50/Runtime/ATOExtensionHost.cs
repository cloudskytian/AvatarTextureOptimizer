// -----------------------------------------------------------------------------
// ATOExtensionHost.cs — public extension surface for advanced users & 3rd-party devs.
// ATOExtensionHost.cs — 面向高级用户与第三方开发者的公开扩展接口。
//
// Third-party code can subscribe to these hooks from their own editor assemblies
// (no reference to ATO's editor assembly needed — the runtime assembly is enough).
// 第三方在自己的 Editor 程序集里订阅即可（无需引用 ATO Editor 程序集，Runtime 即可）。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>Read-only view of a texture that is about to be processed.
    /// 即将被处理的贴图的只读视图。</summary>
    public interface IATOTextureCandidate
    {
        Texture2D Texture { get; }
        /// <summary>Materials referencing it (original, pre-clone). / 引用它的材质（克隆前原件）。</summary>
        IReadOnlyList<Material> ReferencingMaterials { get; }
        /// <summary>Non-empty when ATO plans to skip this texture. / ATO 计划跳过时的原因（非空即跳过）。</summary>
        string SkipReason { get; }
        /// <summary>Request skipping this texture (adds to the whitelist). / 请求跳过该贴图（等价加入白名单）。</summary>
        void Skip(string reason);
    }

    /// <summary>Hook signature / 钩子签名。</summary>
    public delegate void ATOTextureFilterDelegate(IATOTextureCandidate candidate);

    /// <summary>
    /// Runtime-visible host for editor-time extension hooks. Registered once by the editor
    /// assembly; third parties register through it at editor load time.
    /// 编辑期扩展钩子的运行时宿主。由 Editor 程序集注册；第三方在编辑器加载时向其注册。
    /// </summary>
    public static class ATOExtensionHost
    {
        private static readonly List<ATOTextureFilterDelegate> _textureFilters =
            new List<ATOTextureFilterDelegate>();

        /// <summary>Subscribe a texture filter (called once per candidate texture, before processing).
        /// 订阅贴图过滤器（处理前对每个候选贴图调用一次）。</summary>
        public static void RegisterTextureFilter(ATOTextureFilterDelegate filter)
        {
            if (filter != null && !_textureFilters.Contains(filter)) _textureFilters.Add(filter);
        }

        public static void UnregisterTextureFilter(ATOTextureFilterDelegate filter)
        {
            _textureFilters.Remove(filter);
        }

        /// <summary>Invoked by the editor pipeline / 供编辑器管线调用。</summary>
        public static void RunTextureFilters(Action<IATOTextureCandidate> invoke)
        {
            if (_textureFilters.Count == 0) return;
            foreach (var f in _textureFilters) { try { f?.Invoke(default); } catch { /* isolated / 隔离 */ } }
        }

        /// <summary>Number of registered filters / 已注册过滤器数量。</summary>
        public static int FilterCount => _textureFilters.Count;

        // The real per-candidate dispatch is done by the editor assembly which can construct
        // IATOTextureCandidate instances; we keep the list here so third parties only need
        // the runtime assembly. / 逐候选分发由 Editor 程序集执行；列表放这里使第三方只需 Runtime 程序集。
        internal static IReadOnlyList<ATOTextureFilterDelegate> TextureFilters => _textureFilters;
    }
}
