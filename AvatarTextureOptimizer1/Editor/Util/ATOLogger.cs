// ATOLogger.cs / ATOLogger.cs
// Centralised logging and progress reporting for ATO.
// ATO的统一日志与进度报告。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.Editor.Util;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace net.fosa.avatar_texture_optimizer.Editor
{
    /// <summary>
    /// Represents the severity of a log/warning/error for the report.
    /// 表示日志/警告/错误在报告中的严重级别。
    /// </summary>
    public enum ATOLogSeverity
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// A single log entry in the ATO report.
    /// ATO报告中的单条日志条目。
    /// </summary>
    public class ATOLogEntry
    {
        public ATOLogSeverity Severity;
        public string Phase;
        public string Message;
        public double Milliseconds;
    }

    /// <summary>
    /// A disposable phase scope that logs timing.
    /// 一个可Dispose的阶段作用域，记录计时。
    /// </summary>
    public struct ATOPhaseScope : IDisposable
    {
        private readonly ATOLogger _logger;
        private readonly string _phase;
        private readonly Stopwatch _sw;
        private bool _cancelled;

        public ATOPhaseScope(ATOLogger logger, string phaseKey, params object[] args)
        {
            _logger = logger;
            _phase = ATOLocalization.T(phaseKey, args);
            _sw = Stopwatch.StartNew();
            _cancelled = false;
            _logger.LogInfo($"[{_phase}] start / 开始");
            if (_logger._progressCallback != null)
                _logger._progressCallback(_phase, _logger._progress);
        }

        public void Cancel() { _cancelled = true; }

        public void Dispose()
        {
            _sw.Stop();
            if (_cancelled)
            {
                _logger.LogInfo($"[{_phase}] cancelled in {_sw.ElapsedMilliseconds} ms / 取消，耗时 {_sw.ElapsedMilliseconds} ms");
            }
            else
            {
                _logger.LogInfo($"[{_phase}] done in {_sw.ElapsedMilliseconds} ms / 完成，耗时 {_sw.ElapsedMilliseconds} ms", _sw.Elapsed.TotalMilliseconds);
                _logger._progress = Mathf.Clamp01(_logger._progress + _logger._progressStep);
                if (_logger._progressCallback != null)
                    _logger._progressCallback(_phase, _logger._progress);
            }
        }
    }

    /// <summary>
    /// Central logger collecting ATO processing events for the NDMF console and final report.
    /// 集中式日志收集器，收集ATO处理事件供NDMF控制台和最终报告使用。
    /// </summary>
    public class ATOLogger
    {
        private readonly bool _verbose;
        private readonly List<ATOLogEntry> _entries = new();
        private readonly List<(string atlasName, int size, int islandCount, float utilization, long originalBytes, long atlasBytes)> _atlasStats = new();
        private readonly Stopwatch _totalSw = new();

        internal Action<string, float> _progressCallback;
        internal float _progress;
        internal float _progressStep;

        // Stats / 统计
        public int IslandsProcessed;
        public int TexturesSkipped;
        public int MaterialsDedup;
        public int TexturesDedup;
        public int AtlasCount;
        public long OriginalBytes;
        public long OptimizedBytes;

        public ATOLogger(bool verbose)
        {
            _verbose = verbose;
            _totalSw.Start();
        }

        public void StartTotal() { _totalSw.Restart(); }

        public void SetProgressCallback(Action<string, float> callback, int expectedPhases = 10)
        {
            _progressCallback = callback;
            _progressStep = expectedPhases > 0 ? 1.0f / expectedPhases : 0.1f;
            _progress = 0f;
        }

        public ATOPhaseScope Phase(string phaseKey)
        {
            return new ATOPhaseScope(this, phaseKey);
        }

        public void LogInfo(string msg, double ms = 0)
        {
            if (_verbose) Debug.Log($"[ATO] {msg}");
            _entries.Add(new ATOLogEntry { Severity = ATOLogSeverity.Info, Message = msg, Milliseconds = ms });
        }

        public void LogWarning(string msg, UnityEngine.Object context = null)
        {
            Debug.LogWarning($"[ATO] {msg}", context);
            _entries.Add(new ATOLogEntry { Severity = ATOLogSeverity.Warning, Message = msg });
        }

        public void LogError(string msg, UnityEngine.Object context = null)
        {
            Debug.LogError($"[ATO] {msg}", context);
            _entries.Add(new ATOLogEntry { Severity = ATOLogSeverity.Error, Message = msg });
        }

        public void AddAtlasStat(string name, int size, int islandCount, float utilization, long origBytes, long newBytes)
        {
            _atlasStats.Add((name, size, islandCount, utilization, origBytes, newBytes));
            AtlasCount++;
        }

        /// <summary>
        /// Builds a human-readable summary to log after build.
        /// 构建后生成可读摘要并记录日志。
        /// </summary>
        public void EmitFinalReport(BuildContext context)
        {
            _totalSw.Stop();
            var sb = new StringBuilder();
            string title = ATOLocalization.T("report.title");
            string summary = ATOLocalization.T("report.summary");

            sb.AppendLine($"=== {title} ===");
            sb.AppendLine($"{ATOLocalization.T("report.timeTotal")} {_totalSw.ElapsedMilliseconds} ms / {_totalSw.ElapsedMilliseconds} 毫秒");
            sb.AppendLine($"{ATOLocalization.T("report.atlasCount")} {AtlasCount}");
            sb.AppendLine($"{ATOLocalization.T("report.islandsProcessed")} {IslandsProcessed}");
            sb.AppendLine($"{ATOLocalization.T("report.texturesSkipped")} {TexturesSkipped}");
            sb.AppendLine($"{ATOLocalization.T("report.materialsDedup")} {MaterialsDedup}");
            sb.AppendLine($"{ATOLocalization.T("report.texturesDedup")} {TexturesDedup}");
            if (OriginalBytes > 0)
            {
                var saved = OriginalBytes - OptimizedBytes;
                var ratio = OriginalBytes > 0 ? (100.0 * saved / OriginalBytes) : 0;
                sb.AppendLine($"{ATOLocalization.T("report.originalTexSize")} {FormatBytes(OriginalBytes)}");
                sb.AppendLine($"{ATOLocalization.T("report.optimizedTexSize")} {FormatBytes(OptimizedBytes)}");
                sb.AppendLine($"{ATOLocalization.T("report.savedRatio")} {FormatBytes(saved)} ({ratio:F1}%)");
            }
            if (_verbose && _atlasStats.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"--- {ATOLocalization.T("report.detailAtlasses")} ---");
                foreach (var a in _atlasStats)
                {
                    sb.AppendLine($"  {a.atlasName}  {a.size}x{a.size}  islands={a.islandCount}  util={a.utilization*100:F1}%  {FormatBytes(a.originalBytes)} -> {FormatBytes(a.atlasBytes)}");
                }
            }

            Debug.Log($"[ATO] {sb}");

            // Register warnings/errors in NDMF error report / 在NDMF错误报告中注册警告和错误
            int warnCount = 0, errCount = 0;
            foreach (var e in _entries)
            {
                if (e.Severity == ATOLogSeverity.Warning) warnCount++;
                else if (e.Severity == ATOLogSeverity.Error) errCount++;
            }

            if (errCount > 0 || warnCount > 0)
            {
                var errs = new SimpleATOReportError(sb.ToString(), _entries);
                context.ErrorReport.AddError(errs);
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }
    }

    /// <summary>
    /// Minimal IError implementation for final NDMF report.
    /// 最终NDMF报告用的最小IError实现。
    /// </summary>
    internal class SimpleATOReportError : IError
    {
        private readonly string _title;
        private readonly string _details;
        private readonly ErrorSeverity _severity;
        private readonly List<ObjectReference> _refs = new();

        public SimpleATOReportError(string title, List<ATOLogEntry> entries)
        {
            _title = title;
            var sb = new StringBuilder();
            bool hasError = false;
            int warnCount = 0, errCount = 0;
            foreach (var e in entries)
            {
                if (e.Severity == ATOLogSeverity.Error) { hasError = true; errCount++; }
                else if (e.Severity == ATOLogSeverity.Warning) warnCount++;
                if (e.Severity == ATOLogSeverity.Warning || e.Severity == ATOLogSeverity.Error)
                    sb.AppendLine(e.Message);
            }
            _details = sb.Length == 0 ? title : sb.ToString();
            _severity = hasError ? ErrorSeverity.Error : (warnCount > 0 ? ErrorSeverity.NonFatal : ErrorSeverity.Information);
        }

        public ErrorSeverity Severity => _severity;
        public string ToMessage() => _title;
        public void AddReference(ObjectReference obj) => _refs.Add(obj);

        public UnityEngine.UIElements.VisualElement CreateVisualElement(ErrorReport report)
        {
            var ve = new UnityEngine.UIElements.VisualElement();
            var label = new UnityEngine.UIElements.Label(_details);
            label.style.whiteSpace = UnityEngine.UIElements.WhiteSpace.Normal;
            ve.Add(label);
            return ve;
        }
    }
}
