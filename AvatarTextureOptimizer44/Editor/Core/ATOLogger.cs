// ATOLogger.cs - Central [ATO] logging with switches & step timing. / 统一的 [ATO] 日志，带开关与步骤计时。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Debug = UnityEngine.Debug;

namespace Fosa.ATO.Editor.Core
{
    /// <summary>Log switch holder, set from the component each run. / 日志开关持有者，每次运行从组件同步。</summary>
    public static class ATOLog
    {
        /// <summary>Verbose detail logs. / 详细日志。</summary>
        public static bool Verbose;
        /// <summary>Per-step timings. / 每步计时。</summary>
        public static bool Timings;
        /// <summary>Import settings dumps. / 导入设置转储。</summary>
        public static bool ImportSettings;

        /// <summary>Accumulated timing rows for the final report. / 用于最终报告的累计计时行。</summary>
        public static readonly List<(string step, double ms)> TimingsSnapshot = new List<(string, double)>();

        public static void Reset()
        {
            TimingsSnapshot.Clear();
        }

        public static void Info(string msg) => Debug.Log("[ATO] " + msg);
        public static void Warn(string msg) => Debug.LogWarning("[ATO] " + msg);
        public static void Error(string msg) => Debug.LogError("[ATO] " + msg);

        /// <summary>Verbose log, only when enabled. / 详细日志，仅在开启时输出。</summary>
        public static void Detail(string msg) { if (Verbose) Debug.Log("[ATO][V] " + msg); }

        /// <summary>Timed scope: using (ATOLog.Scope("step")) / 计时作用域。</summary>
        public static StepScope Scope(string name) => new StepScope(name);

        public readonly struct StepScope : IDisposable
        {
            private readonly string _name;
            private readonly long _start;
            internal StepScope(string name) { _name = name; _start = Stopwatch.GetTimestamp(); }
            public void Dispose()
            {
                double ms = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
                TimingsSnapshot.Add((_name, ms));
                if (Timings) Info($"{_name}: {ms:F1} ms");
            }
        }

        /// <summary>Format a byte count human readable. / 人类可读的字节数。</summary>
        public static string FormatBytes(long bytes)
        {
            string[] u = { "B", "KB", "MB", "GB" };
            double v = bytes; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:F1} {u[i]}";
        }
    }

    /// <summary>Indented log block helper. / 缩进日志块助手。</summary>
    public static class LogBlock
    {
        public static void Dump(string title, IEnumerable<string> lines)
        {
            var sb = new StringBuilder("[ATO] ").Append(title).AppendLine();
            foreach (var l in lines) sb.Append("  ").AppendLine(l);
            Debug.Log(sb.ToString());
        }
    }
}
