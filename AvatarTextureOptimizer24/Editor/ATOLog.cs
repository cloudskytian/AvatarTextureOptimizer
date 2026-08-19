// ============================================================================
// ATOLog.cs — 统一日志（前缀 [ATO]）/ Unified logging with [ATO] prefix
// (EN) All logs are prefixed with [ATO]. Includes per-step timing helpers and a
//      verbose toggle reserved for advanced users/debugging.
// (ZH) 所有日志带 [ATO] 前缀，含分步计时与供高级用户调试的详细日志开关。
// ============================================================================

using System;
using System.Diagnostics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public static class ATOLog
    {
        // 详细日志开关（供高级用户调试）/ verbose switch for advanced users
        public static bool Verbose = true;

        private const string Prefix = "[ATO]";

        public static void Info(string msg) => UnityEngine.Debug.Log($"{Prefix} {msg}");
        public static void Warn(string msg) => UnityEngine.Debug.LogWarning($"{Prefix} {msg}");
        public static void Error(string msg) => UnityEngine.Debug.LogError($"{Prefix} {msg}");
        public static void VerboseLog(string msg) { if (Verbose) UnityEngine.Debug.Log($"{Prefix} [V] {msg}"); }

        /// <summary>(EN) Time a scoped section and log elapsed ms. (ZH) 计时一段代码并输出耗时（毫秒）。</summary>
        public static Scope Time(string section)
        {
            return new Scope(section);
        }

        public readonly struct Scope : IDisposable
        {
            private readonly string _section;
            private readonly Stopwatch _sw;

            public Scope(string section)
            {
                _section = section;
                _sw = Stopwatch.StartNew();
                VerboseLog($"START {_section}");
            }

            public void Dispose()
            {
                _sw.Stop();
                Info($"END {_section} — {_sw.Elapsed.TotalMilliseconds:F1} ms");
            }
        }
    }
}
