// ============================================================================
// ATO - logging
// ATO - 日志
//
// All console output is prefixed with "[ATO]". A per-build log buffer
// collects every line (with category, elapsed time) so the final report can
// show a summary by default and the full detail when the user enables
// verbose logging. Log categories are controlled by ATOComponent.LogMask and
// VerboseLogging so advanced users can quiet or expand the output.
// 所有控制台输出以 "[ATO]" 前缀。每次构建的日志缓冲区收集每一行（含类别、耗
// 时），最终报告默认显示摘要，用户开启详细日志时显示全部细节。日志类别由
// ATOComponent.LogMask 与 VerboseLogging 控制，便于高级用户收敛或展开输出。
// ============================================================================

#region

using System;
using System.Collections.Generic;
using System.Diagnostics;
using net.fosa.AvatarTextureOptimizer;
using UnityEditor;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Core
{
    public enum ATOLogLevel { Info = 0, Warn = 1, Error = 2 }

    public readonly struct ATOLogEntry
    {
        public readonly double ElapsedMs;
        public readonly ATOLogLevel Level;
        public readonly ATOLogMask Category;
        public readonly string Message;

        public ATOLogEntry(double elapsedMs, ATOLogLevel level, ATOLogMask category, string message)
        {
            ElapsedMs = elapsedMs;
            Level = level;
            Category = category;
            Message = message;
        }
    }

    /// <summary>Scoped, stopwaching [ATO] logger for one build.
    /// 单次构建的作用域计时 [ATO] 日志器。</summary>
    public sealed class ATOLog : IDisposable
    {
        public Stopwatch StopWatch { get; } = Stopwatch.StartNew();
        public ATOLogMask Mask { get; }
        public bool Verbose { get; }

        /// <summary>Per-build ring buffer of every emitted line.
        /// 本次构建的全部日志行。</summary>
        public readonly List<ATOLogEntry> Entries = new();

        private bool _disposed;

        public ATOLog(ATOLogMask mask, bool verbose)
        {
            Mask = mask;
            Verbose = verbose;
        }

        public double NowMs => StopWatch.Elapsed.TotalMilliseconds;

        public bool Enabled(ATOLogMask category) => (Mask & category) != 0;

        public void Log(ATOLogMask category, ATOLogLevel level, string message, bool force = false)
        {
            if (_disposed) return;
            if (!force && level == ATOLogLevel.Info && !Enabled(category)) return;
            if (!force && level == ATOLogLevel.Info && category == ATOLogMask.Verbose && !Verbose) return;

            var entry = new ATOLogEntry(NowMs, level, category, message);
            lock (Entries)
            {
                Entries.Add(entry);
            }

            var prefix = $"[ATO] {NowMs,9:F0}ms {category,-8} ";
            switch (level)
            {
                case ATOLogLevel.Warn:
                    Debug.LogWarning(prefix + message);
                    break;
                case ATOLogLevel.Error:
                    Debug.LogError(prefix + message);
                    break;
                default:
                    Debug.Log(prefix + message);
                    break;
            }
        }

        public void Info(ATOLogMask category, string message) => Log(category, ATOLogLevel.Info, message);
        public void Warn(ATOLogMask category, string message) => Log(category, ATOLogLevel.Warn, message, force: true);
        public void Error(ATOLogMask category, string message) => Log(category, ATOLogLevel.Error, message, force: true);
        public void V(ATOLogMask category, string message) => Log(ATOLogMask.Verbose, ATOLogLevel.Info, $"[{category}] {message}");

        /// <summary>Renders the buffered log as report text.
        /// 将缓冲日志渲染为报告文本。</summary>
        public string RenderAll()
        {
            var sb = new System.Text.StringBuilder();
            lock (Entries)
            {
                foreach (var e in Entries)
                {
                    sb.Append(e.ElapsedMs, 10).Append("ms ").Append(e.Category.ToString(), 8).Append("  ")
                        .Append(e.Message).Append('\n');
                }
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopWatch.Stop();
        }
    }
}
