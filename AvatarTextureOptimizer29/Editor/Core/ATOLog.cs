// [ATO] prefixed logging with timing scopes and per-step durations.
// [ATO] 前缀日志，含计时作用域与每步耗时统计。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace net.fosa.ato.editor
{
    internal static class ATOLog
    {
        // Configured at bake start from the component. / 烘焙开始时按组件设置。
        internal static AtoLogLevel Level = AtoLogLevel.Info;

        private static readonly List<(string stage, double ms)> Timings = new List<(string, double)>();

        internal static void ResetTimings() => Timings.Clear();

        [Conditional("ATO_ALWAYS")]
        private static void Raw(string lv, string msg)
        {
            Debug.Log($"[ATO][{lv}] {msg}");
        }

        internal static void Info(string msg)
        {
            if (Level >= AtoLogLevel.Info) Debug.Log($"[ATO] {msg}");
        }

        internal static void Warn(string msg)
        {
            // warnings always visible / 警告始终可见
            Debug.LogWarning($"[ATO] {msg}");
        }

        internal static void Error(string msg)
        {
            Debug.LogError($"[ATO] {msg}");
        }

        internal static void DebugL(string msg)
        {
            if (Level >= AtoLogLevel.Debug) Debug.Log($"[ATO][dbg] {msg}");
        }

        internal static void Trace(string msg)
        {
            if (Level >= AtoLogLevel.Trace) Debug.Log($"[ATO][trc] {msg}");
        }

        /// <summary>Timing scope: using (ATOLog.Scope("stage")) { ... } / 计时作用域。</summary>
        internal static StageScope Scope(string name) => new StageScope(name);

        internal readonly struct StageScope : IDisposable
        {
            private readonly string _name;
            private readonly Stopwatch _sw;

            internal StageScope(string name)
            {
                _name = name;
                _sw = Stopwatch.StartNew();
                if (Level >= AtoLogLevel.Debug) Debug.Log($"[ATO] >>> {_name} ...");
            }

            public void Dispose()
            {
                _sw.Stop();
                Timings.Add((_name, _sw.Elapsed.TotalMilliseconds));
                if (Level >= AtoLogLevel.Debug)
                    Debug.Log($"[ATO] <<< {_name} done in {_sw.Elapsed.TotalMilliseconds:F1} ms");
            }
        }

        /// <summary>Snapshot of collected stage timings (stage, ms). / 已收集的阶段耗时。</summary>
        internal static IReadOnlyList<(string stage, double ms)> StageTimings => Timings;
    }
}
