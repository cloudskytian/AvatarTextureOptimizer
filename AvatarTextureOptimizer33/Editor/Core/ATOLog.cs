// SPDX-License-Identifier: MIT
// EN: Logging, timing and report accumulation for the ATO pipeline.
// ZH: ATO 管线的日志、计时与报告累积。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Debug = UnityEngine.Debug;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: Severity of a line inside the ATO build report.
    /// ZH: ATO 构建报告中每一行的级别。
    /// </summary>
    public enum ATOLogLevel
    {
        Trace = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
    }

    /// <summary>
    /// EN: One recorded log line.
    /// ZH: 一条记录下来的日志。
    /// </summary>
    public sealed class ATOLogEntry
    {
        public ATOLogLevel Level;
        public string Category;
        public string Message;
        public double ElapsedMs;
    }

    /// <summary>
    /// EN: A scoped stopwatch; disposing records the elapsed time under <see cref="Name"/>.
    /// ZH: 作用域计时器；Dispose 时把耗时记录到 <see cref="Name"/> 名下。
    /// </summary>
    public sealed class ATOTimingScope : IDisposable
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly ATOLog _log;
        public readonly string Name;

        internal ATOTimingScope(ATOLog log, string name)
        {
            _log = log;
            Name = name;
        }

        public void Dispose()
        {
            _sw.Stop();
            _log.RecordTiming(Name, _sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// EN: Central log sink. Everything is prefixed with [ATO]; the console output can be switched off
    ///     while the in-report history is always kept so the final NDMF report stays complete.
    /// ZH: 集中式日志。所有输出都以 [ATO] 开头；控制台输出可关闭，但报告内的历史始终保留，
    ///     以保证最终的 NDMF 报告完整。
    /// </summary>
    public sealed class ATOLog
    {
        public const string Prefix = "[ATO]";

        private readonly List<ATOLogEntry> _entries = new List<ATOLogEntry>();
        private readonly Dictionary<string, double> _timings = new Dictionary<string, double>();
        private readonly List<string> _timingOrder = new List<string>();
        private readonly Stopwatch _global = Stopwatch.StartNew();

        /// <summary>EN: Mirror trace logs to the Unity console. ZH: 是否把 Trace 级日志同步到控制台。</summary>
        public bool Verbose;

        /// <summary>EN: All recorded entries. ZH: 全部记录。</summary>
        public IReadOnlyList<ATOLogEntry> Entries => _entries;

        /// <summary>EN: Ordered per step timings in milliseconds. ZH: 按顺序排列的每步耗时（毫秒）。</summary>
        public IReadOnlyList<KeyValuePair<string, double>> Timings
        {
            get
            {
                var list = new List<KeyValuePair<string, double>>(_timingOrder.Count);
                foreach (var k in _timingOrder) list.Add(new KeyValuePair<string, double>(k, _timings[k]));
                return list;
            }
        }

        /// <summary>EN: Total elapsed time of the whole run. ZH: 整个流程的总耗时。</summary>
        public double TotalMs => _global.Elapsed.TotalMilliseconds;

        public ATOTimingScope Step(string name) => new ATOTimingScope(this, name);

        internal void RecordTiming(string name, double ms)
        {
            if (!_timings.ContainsKey(name))
            {
                _timings[name] = 0;
                _timingOrder.Add(name);
            }

            _timings[name] += ms;
            Trace("timing", $"{name}: {ms:F1} ms");
        }

        public void Trace(string category, string message) => Write(ATOLogLevel.Trace, category, message);
        public void Info(string category, string message) => Write(ATOLogLevel.Info, category, message);
        public void Warning(string category, string message) => Write(ATOLogLevel.Warning, category, message);
        public void Error(string category, string message) => Write(ATOLogLevel.Error, category, message);

        private void Write(ATOLogLevel level, string category, string message)
        {
            var e = new ATOLogEntry
            {
                Level = level,
                Category = category,
                Message = message,
                ElapsedMs = _global.Elapsed.TotalMilliseconds,
            };
            _entries.Add(e);

            var line = $"{Prefix}[{category}] {message}";
            switch (level)
            {
                case ATOLogLevel.Trace:
                    if (Verbose) Debug.Log(line);
                    break;
                case ATOLogLevel.Info:
                    if (Verbose) Debug.Log(line);
                    break;
                case ATOLogLevel.Warning:
                    Debug.LogWarning(line);
                    break;
                case ATOLogLevel.Error:
                    Debug.LogError(line);
                    break;
            }
        }

        /// <summary>
        /// EN: Renders the collected details as a plain text block used by the NDMF report.
        /// ZH: 把收集到的细节渲染成纯文本块，供 NDMF 报告使用。
        /// </summary>
        public string BuildDetailText(int maxLines = 4000)
        {
            var sb = new StringBuilder();
            var count = 0;
            foreach (var e in _entries)
            {
                if (count++ > maxLines)
                {
                    sb.AppendLine("... (truncated)");
                    break;
                }

                sb.Append('[').Append(e.ElapsedMs.ToString("F0")).Append("ms][").Append(e.Category).Append("] ");
                if (e.Level == ATOLogLevel.Warning) sb.Append("WARN: ");
                else if (e.Level == ATOLogLevel.Error) sb.Append("ERROR: ");
                sb.AppendLine(e.Message);
            }

            return sb.ToString();
        }
    }
}
