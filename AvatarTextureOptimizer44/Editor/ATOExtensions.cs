// ATOExtensions.cs - Public extension points for advanced users & third-party developers.
// 面向高级用户与第三方开发者的公开扩展点。
// Hooks are invoked at every pipeline stage; a hook may mutate the shared objects freely.
// 钩子在每个管线阶段被调用；钩子可以自由修改共享对象。
using System;
using System.Collections.Generic;
using Fosa.ATO.Editor.Analysis;
using Fosa.ATO.Editor.Atlas;

namespace Fosa.ATO.Editor
{
    /// <summary>Extension hook interface. / 扩展钩子接口。</summary>
    public interface IATOExtension
    {
        string Name { get; }
        /// <summary>Called with the stage tag and the stage payload. / 以阶段标记与负载调用。</summary>
        void OnStage(ATOStage stage, object payload);
    }

    public enum ATOStage
    {
        GraphBuilt,        // payload: UsageGraph / 使用图
        QualityDone,       // payload: UsageGraph / 质量完成
        Packed,            // payload: PackResult / 装箱完成
        Rendered,          // payload: List<AtlasImage> / 渲染完成
        Rewritten,         // payload: RewriteResult / 改写完成
        Finished,          // payload: ATOReport / 全部完成
    }

    /// <summary>Registry for extensions. / 扩展注册表。</summary>
    public static class ATOExtensions
    {
        private static readonly List<IATOExtension> _ext = new List<IATOExtension>();
        public static IReadOnlyList<IATOExtension> All => _ext;

        public static void Register(IATOExtension e)
        {
            if (e != null && !_ext.Contains(e)) _ext.Add(e);
        }

        public static void Unregister(IATOExtension e) => _ext.Remove(e);

        /// <summary>Fire a stage event; exceptions never break the build. / 触发阶段事件；异常不会中断构建。</summary>
        public static void Fire(ATOStage stage, object payload)
        {
            foreach (var e in _ext.ToArray())
            {
                try { e.OnStage(stage, payload); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ATO] extension {e.Name} threw at {stage}: {ex}"); }
            }
        }
    }
}
