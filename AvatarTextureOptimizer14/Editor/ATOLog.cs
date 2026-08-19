// ATOLog — logging & timing / 日志与计时
// All logs are prefixed with [ATO]; verbose logs are gated by the component switch.<br>
// 所有日志以 [ATO] 开头；详细日志由组件开关控制，便于未来高级用户调试。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Fosa.ATO.Editor
{
    /// <summary>Centralized [ATO] logging with per-stage timing. / 统一 [ATO] 日志与分阶段计时。</summary>
    internal static class ATOLog
    {
        internal static bool Verbose;      // synced from component / 与组件开关同步
        private static readonly List<(string stage, long ms)> _stageTimes = new List<(string, long)>();

        internal sealed class StageScope : IDisposable
        {
            private readonly string _name; private readonly Stopwatch _sw = Stopwatch.StartNew();
            internal StageScope(string name) { _name = name; Info($"→ {name}"); }
            public void Dispose() { _sw.Stop(); _stageTimes.Add((_name, _sw.ElapsedMilliseconds)); Info($"← {_name} done in {_sw.ElapsedMilliseconds} ms"); }
        }

        internal static StageScope Stage(string name) => new StageScope(name);
        internal static IReadOnlyList<(string stage, long ms)> StageTimes => _stageTimes;
        internal static void Reset() { _stageTimes.Clear(); Verbose = false; }

        internal static void Info(string msg) => Debug.Log($"[ATO] {msg}");
        internal static void V(string msg) { if (Verbose) Debug.Log($"[ATO] [v] {msg}"); }
        internal static void Warn(string msg) => Debug.LogWarning($"[ATO] {msg}");
        internal static void Error(string msg) => Debug.LogError($"[ATO] {msg}");
    }
}
