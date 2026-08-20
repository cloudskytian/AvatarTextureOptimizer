using System;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Fosa.Ato.Editor
{
    /// <summary>
    /// Centralized logger. All messages are prefixed [ATO]. Supports verbose mode and per-stage
    /// timing via <see cref="Timed"/>. Designed to be called early/often (instrument everywhere,
    /// not after bugs appear).
    /// 集中式日志。全部以 [ATO] 开头，支持详细模式与分阶段耗时统计。
    /// </summary>
    internal static class AtoLog
    {
        public const string Tag = "[ATO]";
        public static bool Verbose;

        public static void Info(string msg) => Debug.Log($"{Tag} {msg}");
        public static void Warn(string msg) => Debug.LogWarning($"{Tag} {msg}");
        public static void Error(string msg) => Debug.LogError($"{Tag} {msg}");
        public static void Error(Exception e, string msg) => Debug.LogError($"{Tag} {msg}\n{e}");

        [Conditional("ATO_VERBOSE")]
        public static void V(string msg)
        {
            if (Verbose) Debug.Log($"{Tag} {msg}");
        }

        public static void VIf(bool on, string msg)
        {
            if (on || Verbose) Debug.Log($"{Tag} {msg}");
        }

        /// <summary>Run an action, log its elapsed milliseconds, and return its result. / 运行并打印耗时。</summary>
        public static T Timed<T>(string stage, Func<T> action)
        {
            var sw = Stopwatch.StartNew();
            try { return action(); }
            finally { sw.Stop(); Debug.Log($"{Tag} ⏱ {stage}: {sw.ElapsedMilliseconds} ms"); }
        }

        /// <summary>Run an action and log its elapsed milliseconds. / 运行并打印耗时。</summary>
        public static void Timed(string stage, Action action)
        {
            var sw = Stopwatch.StartNew();
            try { action(); }
            finally { sw.Stop(); Debug.Log($"{Tag} ⏱ {stage}: {sw.ElapsedMilliseconds} ms"); }
        }

        /// <summary>Scoped timer for using() blocks. / using 作用域计时器。</summary>
        public readonly struct Scope : IDisposable
        {
            private readonly string _stage;
            private readonly Stopwatch _sw;
            private readonly bool _always;
            public Scope(string stage, bool always = true)
            {
                _stage = stage; _always = always;
                _sw = Stopwatch.StartNew();
            }
            public void Dispose()
            {
                _sw.Stop();
                if (_always || Verbose) Debug.Log($"{Tag} ⏱ {_stage}: {_sw.ElapsedMilliseconds} ms");
            }
        }
    }
}
