// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Central logging helper. All ATO logs start with the [ATO] prefix and carry a
    /// verbosity switch so advanced users can enable per-step timing.
    ///
    /// 统一日志工具。所有 ATO 日志以 [ATO] 开头，并提供级别开关供高级用户开启逐步计时。
    /// </summary>
    public static class ATOLog
    {
        /// <summary>Global verbosity: 0=quiet, 1=normal, 2=verbose. 全局级别：0=安静 1=正常 2=详细。</summary>
        public static int Level = 1;

        /// <summary>
        /// When true, every step additionally logs its elapsed time.
        /// 为 true 时，每一步额外记录耗时。
        /// </summary>
        public static bool LogTimings = true;

        /// <summary>Log info (level 1). 普通信息（级别 1）。</summary>
        public static void Info(string msg)
        {
            if (Level >= 1) Debug.Log("[ATO] " + msg);
        }

        /// <summary>Log verbose detail (level 2). 详细信息（级别 2）。</summary>
        public static void Verbose(string msg)
        {
            if (Level >= 2) Debug.Log("[ATO] " + msg);
        }

        /// <summary>Log a warning. 警告。</summary>
        public static void Warning(string msg)
        {
            Debug.LogWarning("[ATO] " + msg);
        }

        /// <summary>Log an error. 错误。</summary>
        public static void Error(string msg)
        {
            Debug.LogError("[ATO] " + msg);
        }

        /// <summary>
        /// Log a step with a stopwatch label (timing included). 记录一个带计时的步骤。
        /// </summary>
        public static void Step(string msg)
        {
            if (Level >= 1)
                Debug.Log($"[ATO] [STEP] {msg}");
        }

        /// <summary>
        /// Scoped timing helper. Usage: `using var _ = ATOLog.Time("my step");`
        /// Logs elapsed ms on dispose. 作用域计时：退出作用域时打印耗时（毫秒）。
        /// </summary>
        public static Scope Time(string label)
        {
            return new Scope(label);
        }

        public readonly struct Scope : IDisposable
        {
            private readonly Stopwatch _sw;
            private readonly string _label;

            public Scope(string label)
            {
                _label = label;
                _sw = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                _sw.Stop();
                if (LogTimings)
                    Debug.Log($"[ATO] [TIMING] {_label}: {_sw.Elapsed.TotalMilliseconds:F1} ms");
            }
        }
    }
}
