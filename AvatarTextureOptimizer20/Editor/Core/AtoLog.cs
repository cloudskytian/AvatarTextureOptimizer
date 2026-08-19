// [ATO] logging with timing scopes. / 带耗时统计的日志工具。
using System;
using System.Diagnostics;
using Debug = UnityEngine.Debug;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// All logs are prefixed with [ATO]. Verbose logging is switchable for advanced users.
    /// 所有日志以 [ATO] 开头；verbose 开关供高级用户调试。
    /// </summary>
    public static class AtoLog
    {
        public static bool Verbose = true;

        public static void Info(string msg) => Debug.Log("[ATO] " + msg);
        public static void Debugf(string msg) { if (Verbose) Debug.Log("[ATO][debug] " + msg); }
        public static void Warn(string msg) => Debug.LogWarning("[ATO] " + msg);
        public static void Error(string msg) => Debug.LogError("[ATO] " + msg);

        /// <summary>Timed scope; logs elapsed ms on dispose. / 计时作用域，Dispose 时输出耗时。</summary>
        public sealed class Scope : IDisposable
        {
            private readonly string _label;
            private readonly Stopwatch _sw = Stopwatch.StartNew();
            private readonly Action<string, long> _onDone;

            public Scope(string label, Action<string, long> onDone = null)
            {
                _label = label;
                _onDone = onDone;
                Debugf($"begin: {label}");
            }

            public long ElapsedMs => _sw.ElapsedMilliseconds;

            public void Dispose()
            {
                _sw.Stop();
                Debugf($"end: {_label} ({_sw.ElapsedMilliseconds} ms)");
                _onDone?.Invoke(_label, _sw.ElapsedMilliseconds);
            }
        }

        public static Scope Time(string label, Action<string, long> onDone = null) => new Scope(label, onDone);
    }
}
