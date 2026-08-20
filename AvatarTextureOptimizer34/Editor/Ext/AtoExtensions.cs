// AvatarTextureOptimizer - AtoExtensions
// EN: Extension points for advanced users & third-party developers.
// CN: 供高级用户与第三方开发者使用的扩展点。
using System;
using System.Collections.Generic;
using UnityEditor;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// EN: Implement and the build will invoke your hooks. Register via [InitializeOnLoadMethod] + AtoExtensions.Register.
    /// CN: 实现本接口后构建会调用你的钩子。经 [InitializeOnLoadMethod] + AtoExtensions.Register 注册。
    /// </summary>
    public interface IAtoExtension
    {
        /// <summary>EN: Before analysis starts. / CN: 分析开始前。</summary>
        void OnBeforeAnalyze(AtoBuildState state);
        /// <summary>EN: After baking, before remapping. / CN: 烘焙后、重映射前。</summary>
        void OnAfterBake(AtoBuildState state, PackingResult packing);
        /// <summary>EN: After everything (before report). / CN: 全部完成后（报告前）。</summary>
        void OnAfterAll(AtoBuildState state);
    }

    /// <summary>EN: Extension registry (also auto-discovers IAtoExtension implementations). / CN: 扩展注册表（自动发现 IAtoExtension 实现）。</summary>
    public static class AtoExtensions
    {
        private static readonly List<IAtoExtension> _extensions = new List<IAtoExtension>();
        private static bool _discovered;

        public static void Register(IAtoExtension ext)
        {
            if (ext != null && !_extensions.Contains(ext)) _extensions.Add(ext);
        }

        public static void Unregister(IAtoExtension ext) => _extensions.Remove(ext);

        private static void AutoDiscover()
        {
            if (_discovered) return;
            _discovered = true;
            try
            {
                foreach (var type in TypeCache.GetTypesDerivedFrom<IAtoExtension>())
                {
                    if (type.IsAbstract || type.IsInterface) continue;
                    try { Register((IAtoExtension)Activator.CreateInstance(type)); }
                    catch (Exception e) { AtoLog.Detail($"Extension {type.Name} init failed: {e.Message}"); }
                }
            }
            catch (Exception) { }
        }

        public static void InvokeBeforeAnalyze(AtoBuildState state)
        {
            AutoDiscover();
            foreach (var ext in _extensions) TryCall(() => ext.OnBeforeAnalyze(state), ext.GetType().Name);
        }

        public static void InvokeAfterBake(AtoBuildState state, PackingResult packing)
        {
            foreach (var ext in _extensions) TryCall(() => ext.OnAfterBake(state, packing), ext.GetType().Name);
        }

        public static void InvokeAfterAll(AtoBuildState state)
        {
            foreach (var ext in _extensions) TryCall(() => ext.OnAfterAll(state), ext.GetType().Name);
        }

        private static void TryCall(Action action, string name)
        {
            try { action(); }
            catch (Exception e) { AtoLog.Warn($"Extension {name} failed: {e.Message}"); }
        }
    }
}
