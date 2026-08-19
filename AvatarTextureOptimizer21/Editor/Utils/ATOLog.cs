// ATO Logger - Logging utility for debug and report output
// ATO日志器 - 调试和报告输出的日志工具

using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Core
{
    /// <summary>
    /// Centralized logging for ATO with [ATO] prefix and timing support.
    /// ATO的集中式日志记录，带[ATO]前缀和计时支持。
    /// </summary>
    public static class ATOLog
    {
        private static bool _verboseEnabled = false;
        private static List<string> _pendingWarnings = new List<string>();
        private static List<ReportEntry> _reportEntries = new List<ReportEntry>();

        public static void SetVerbose(bool enabled) => _verboseEnabled = enabled;

        public static void Info(string message)
        {
            Debug.Log($"[ATO] {message}");
        }

        public static void Verbose(string message)
        {
            if (_verboseEnabled)
                Debug.Log($"[ATO][VERBOSE] {message}");
        }

        public static void Warning(string message)
        {
            Debug.LogWarning($"[ATO] {message}");
            _pendingWarnings.Add(message);
        }

        public static void Error(string message)
        {
            Debug.LogError($"[ATO] {message}");
        }

        public static void AddReport(ReportEntry entry)
        {
            _reportEntries.Add(entry);
        }

        public static List<ReportEntry> GetReportEntries() => _reportEntries;
        public static List<string> GetPendingWarnings() => _pendingWarnings;

        public static void Clear()
        {
            _pendingWarnings.Clear();
            _reportEntries.Clear();
        }

        /// <summary>
        /// Helper to time a block of code.
        /// 计时代码块的辅助工具。
        /// </summary>
        public class TimedScope : IDisposable
        {
            private readonly string _name;
            private readonly Stopwatch _sw;

            public TimedScope(string name)
            {
                _name = name;
                _sw = Stopwatch.StartNew();
                Verbose($"[{_name}] Starting...");
            }

            public double ElapsedMs => _sw.Elapsed.TotalMilliseconds;

            public void Dispose()
            {
                _sw.Stop();
                Verbose($"[{_name}] Completed in {_sw.Elapsed.TotalMilliseconds:F1}ms");
            }
        }

        public static TimedScope Time(string name) => new TimedScope(name);
    }
}
