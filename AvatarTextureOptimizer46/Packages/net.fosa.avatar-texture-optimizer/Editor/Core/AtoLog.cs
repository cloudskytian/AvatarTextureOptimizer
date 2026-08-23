// SPDX-License-Identifier: MIT
// EN: Central logging facility. Every message is prefixed with [ATO] and can be routed to the build report.
// ZH: 统一日志设施。所有消息均以 [ATO] 开头，并可汇入构建报告。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Core
{
    /// <summary>
    /// EN: Verbosity level of a single log record.
    /// ZH: 单条日志记录的详细级别。
    /// </summary>
    public enum AtoLogLevel
    {
        /// <summary>EN: Per island / per texel detail. ZH: 逐岛、逐像素级别的细节。</summary>
        Trace = 0,
        /// <summary>EN: Step level detail. ZH: 步骤级别的细节。</summary>
        Debug = 1,
        /// <summary>EN: Summary information always worth keeping. ZH: 总是值得保留的概要信息。</summary>
        Info = 2,
        /// <summary>EN: Something was skipped or degraded but the build continues. ZH: 有内容被跳过或降级，但构建继续。</summary>
        Warning = 3,
        /// <summary>EN: The build cannot continue correctly. ZH: 构建无法正确继续。</summary>
        Error = 4,
    }

    /// <summary>
    /// EN: One recorded log line, kept in memory so the final NDMF report can display it.
    /// ZH: 一条记录下来的日志，保存在内存中以便最终的 NDMF 报告展示。
    /// </summary>
    public readonly struct AtoLogRecord
    {
        /// <summary>EN: Severity. ZH: 级别。</summary>
        public readonly AtoLogLevel Level;
        /// <summary>EN: Logical stage name, e.g. "Collect". ZH: 逻辑阶段名，例如 "Collect"。</summary>
        public readonly string Stage;
        /// <summary>EN: Message text. ZH: 消息文本。</summary>
        public readonly string Message;
        /// <summary>EN: Milliseconds since the build started. ZH: 自构建开始起的毫秒数。</summary>
        public readonly double TimestampMs;

        /// <summary>EN: Creates a record. ZH: 创建一条记录。</summary>
        public AtoLogRecord(AtoLogLevel level, string stage, string message, double timestampMs)
        {
            Level = level;
            Stage = stage;
            Message = message;
            TimestampMs = timestampMs;
        }

        /// <inheritdoc/>
        public override string ToString() => $"[ATO][{Stage}] {Message}";
    }

    /// <summary>
    /// EN: Static logger scoped to a single avatar build. Call <see cref="Begin"/> at the start of the
    ///     build and <see cref="End"/> when it finishes (including on cancel).
    /// ZH: 作用域为单次 Avatar 构建的静态日志器。构建开始时调用 <see cref="Begin"/>，
    ///     结束（含取消）时调用 <see cref="End"/>。
    /// </summary>
    public static class AtoLog
    {
        private static readonly List<AtoLogRecord> _records = new List<AtoLogRecord>(1024);
        private static readonly Stopwatch _clock = new Stopwatch();
        private static AtoLogLevel _consoleMinLevel = AtoLogLevel.Info;
        private static string _stage = "-";

        /// <summary>EN: All records collected during the current build. ZH: 当前构建收集到的所有记录。</summary>
        public static IReadOnlyList<AtoLogRecord> Records => _records;

        /// <summary>EN: Total elapsed milliseconds of the current build. ZH: 当前构建的总耗时（毫秒）。</summary>
        public static double ElapsedMs => _clock.Elapsed.TotalMilliseconds;

        /// <summary>
        /// EN: Starts a new logging scope, clearing anything left over from a previous build.
        /// ZH: 开启新的日志作用域，清除上一次构建残留的内容。
        /// </summary>
        /// <param name="verbose">EN: Route Debug records to the console. ZH: 将 Debug 记录输出到控制台。</param>
        /// <param name="trace">EN: Route Trace records to the console. ZH: 将 Trace 记录输出到控制台。</param>
        public static void Begin(bool verbose, bool trace)
        {
            _records.Clear();
            _clock.Restart();
            _consoleMinLevel = trace ? AtoLogLevel.Trace : verbose ? AtoLogLevel.Debug : AtoLogLevel.Info;
            _stage = "-";
            Info("Build", "Avatar Texture Optimizer logging started.");
        }

        /// <summary>EN: Ends the current logging scope. ZH: 结束当前日志作用域。</summary>
        public static void End()
        {
            _clock.Stop();
        }

        /// <summary>
        /// EN: Enters a named stage. Dispose the returned scope to log the elapsed time automatically.
        /// ZH: 进入一个命名阶段。释放返回的作用域时会自动记录耗时。
        /// </summary>
        public static StageScope Stage(string name) => new StageScope(name);

        /// <summary>
        /// EN: Disposable timing scope for one pipeline stage.
        /// ZH: 用于单个管线阶段的可释放计时作用域。
        /// </summary>
        public readonly struct StageScope : IDisposable
        {
            private readonly string _previous;
            private readonly string _name;
            private readonly double _startMs;

            internal StageScope(string name)
            {
                _previous = _stage;
                _name = name;
                _startMs = ElapsedMs;
                _stage = name;
                Debug_(name, "begin");
            }

            /// <summary>EN: Logs the stage duration and restores the previous stage. ZH: 记录阶段耗时并恢复上一个阶段。</summary>
            public void Dispose()
            {
                var ms = ElapsedMs - _startMs;
                Info(_name, $"done in {ms:F1} ms");
                _stage = _previous;
            }
        }

        /// <summary>EN: Measures an arbitrary action and returns its duration in milliseconds. ZH: 测量任意操作并返回其耗时（毫秒）。</summary>
        public static double Measure(string label, Action action)
        {
            var t0 = ElapsedMs;
            action();
            var dt = ElapsedMs - t0;
            Debug_(_stage, $"{label}: {dt:F1} ms");
            return dt;
        }

        /// <summary>EN: Records a trace level message. ZH: 记录一条 trace 级别的消息。</summary>
        public static void Trace(string stage, string message) => Write(AtoLogLevel.Trace, stage, message);

        /// <summary>EN: Records a debug level message. Named with an underscore to avoid clashing with UnityEngine.Debug. ZH: 记录一条 debug 级别的消息。名字带下划线以避免与 UnityEngine.Debug 冲突。</summary>
        public static void Debug_(string stage, string message) => Write(AtoLogLevel.Debug, stage, message);

        /// <summary>EN: Records an informational message. ZH: 记录一条信息级别的消息。</summary>
        public static void Info(string stage, string message) => Write(AtoLogLevel.Info, stage, message);

        /// <summary>EN: Records a warning. ZH: 记录一条警告。</summary>
        public static void Warning(string stage, string message) => Write(AtoLogLevel.Warning, stage, message);

        /// <summary>EN: Records an error. ZH: 记录一条错误。</summary>
        public static void Error(string stage, string message) => Write(AtoLogLevel.Error, stage, message);

        /// <summary>EN: Records an exception with its stack trace. ZH: 记录异常及其调用栈。</summary>
        public static void Exception(string stage, Exception e)
        {
            Write(AtoLogLevel.Error, stage, e.ToString());
        }

        private static void Write(AtoLogLevel level, string stage, string message)
        {
            var record = new AtoLogRecord(level, stage ?? _stage, message, ElapsedMs);
            _records.Add(record);
            if (level < _consoleMinLevel) return;

            var line = $"[ATO][{record.Stage}][{record.TimestampMs:F0}ms] {message}";
            switch (level)
            {
                case AtoLogLevel.Warning:
                    UnityEngine.Debug.LogWarning(line);
                    break;
                case AtoLogLevel.Error:
                    UnityEngine.Debug.LogError(line);
                    break;
                default:
                    UnityEngine.Debug.Log(line);
                    break;
            }
        }

        /// <summary>
        /// EN: Renders all records at or above <paramref name="minLevel"/> into a single string, used by
        ///     the collapsible detail section of the NDMF report.
        /// ZH: 将不低于 <paramref name="minLevel"/> 的所有记录渲染为单个字符串，
        ///     用于 NDMF 报告中可折叠的细节区域。
        /// </summary>
        public static string Dump(AtoLogLevel minLevel)
        {
            var sb = new StringBuilder();
            foreach (var r in _records)
            {
                if (r.Level < minLevel) continue;
                sb.Append('[').Append(r.Stage).Append("] ").Append(r.Message).Append('\n');
            }
            return sb.ToString();
        }
    }
}
