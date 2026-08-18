// Avatar Texture Optimizer (ATO)
// Extensibility point for advanced users and third-party developers.
// 面向高级用户与第三方开发者的扩展点。
//
// Usage: implement IATOStage and call ATOStageRegistry.Register before the build runs
// (e.g. in an [InitializeOnLoadMethod]); the stage runs inside ATOPipeline in the order
// registered. Stage order uses a numeric priority (lower runs first); the built-in stages
// use priorities in the 0..1000 range, so pick 1000+ to run after them.
// 用法：实现 IATOStage 并在构建前调用 ATOStageRegistry.Register（例如在
// [InitializeOnLoadMethod] 中）；阶段按注册顺序在 ATOPipeline 内运行。顺序用数值优先级
// （越小越先）；内置阶段占用 0..1000，故取 1000+ 可在其后运行。

using System;
using System.Collections.Generic;

namespace NetFosa.ATO
{
    /// <summary>
    /// A user-defined pipeline stage. / 用户自定义管线阶段。
    /// </summary>
    public interface IATOStage
    {
        /// <summary>Unique stage id. / 唯一阶段标识。</summary>
        string Id { get; }

        /// <summary>Human-readable stage name (shown in logs). / 可读阶段名（显示于日志）。</summary>
        string Name { get; }

        /// <summary>Lower runs first. Built-in stages use 0..1000. / 越小越先；内置阶段占用 0..1000。</summary>
        int Priority { get; }

        /// <summary>Execute the stage against the build context. / 针对构建上下文执行阶段。</summary>
        void Execute(ATOBuildContext build);
    }

    /// <summary>
    /// Registry for custom stages, invoked by ATOPipeline. / 自定义阶段注册表，由 ATOPipeline 调用。
    /// </summary>
    public static class ATOStageRegistry
    {
        private static readonly List<IATOStage> _stages = new List<IATOStage>();

        public static void Register(IATOStage stage)
        {
            if (stage == null) return;
            if (_stages.Exists(s => s.Id == stage.Id))
            {
                ATOLogger.Warn($"Duplicate ATO stage id '{stage.Id}' ignored. / 重复的 ATO 阶段标识 '{stage.Id}' 已忽略。");
                return;
            }
            _stages.Add(stage);
            _stages.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        public static IReadOnlyList<IATOStage> Stages => _stages;

        internal static void RunAll(ATOBuildContext build)
        {
            foreach (var s in _stages)
            {
                if (build.progress != null && build.progress.IsCancellationRequested) break;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ATOLogger.Info($"=== [ATO] Custom stage '{s.Name}' started ===");
                try { s.Execute(build); }
                catch (Exception e)
                {
                    ATOLogger.Error($"Custom stage '{s.Name}' failed: {e.Message}");
                    throw;
                }
                finally { sw.Stop(); ATOLogger.Step($"Custom stage '{s.Name}'", sw.Elapsed.TotalMilliseconds); }
            }
        }
    }
}
