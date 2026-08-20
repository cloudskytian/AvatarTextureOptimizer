using System;
using System.Diagnostics;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// ATO logging: every message is prefixed with [ATO]. / ATO 日志：所有消息以 [ATO] 开头。
    /// Verbosity is controlled by the per-avatar log level (Summary/Normal/Verbose). /
    /// 详细度由每个 Avatar 的日志级别控制（摘要/常规/详细）。
    /// </summary>
    internal static class AtoLog
    {
        /// <summary>Current log level (set per build from avatar settings). / 当前日志级别（每次构建按 Avatar 设置设置）。</summary>
        public static AtoLogLevel Level { get; set; } = AtoLogLevel.Normal;

        public static void Info(string message)
        {
            if (Level >= AtoLogLevel.Normal) Debug.Log($"[ATO] {message}");
        }

        /// <summary>Log always, even in Summary mode (used for the final summary). / 始终输出（摘要模式也输出，用于最终摘要）。</summary>
        public static void Summary(string message) => Debug.Log($"[ATO] {message}");

        public static void Verbose(string message)
        {
            if (Level >= AtoLogLevel.Verbose) Debug.Log($"[ATO] {message}");
        }

        public static void Warn(string message) => Debug.LogWarning($"[ATO] {message}");

        public static void Error(string message) => Debug.LogError($"[ATO] {message}");

        /// <summary>
        /// Time a block of code and log the elapsed milliseconds. / 对代码块计时并输出耗时（毫秒）。
        /// Usage: using (AtoLog.Time("stage.quality")) { ... } /
        /// 用法：using (AtoLog.Time("stage.quality")) { ... }
        /// </summary>
        public static IDisposable Time(string what)
        {
            return new TimingScope(what);
        }

        private sealed class TimingScope : IDisposable
        {
            private readonly string _what;
            private readonly Stopwatch _sw = Stopwatch.StartNew();

            public TimingScope(string what) => _what = what;

            public void Dispose()
            {
                _sw.Stop();
                Info($"{_what} took {_sw.ElapsedMilliseconds} ms");
            }
        }
    }
}
