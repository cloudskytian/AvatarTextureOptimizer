using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// [ATO] 日志 + 进度 + 取消 + 报告。
    /// 所有日志以 [ATO] 开头；支持耗时统计、折叠式报告、可取消进度条。
    ///
    /// [ATO] logging + progress + cancellation + report.
    /// </summary>
    public static class ATOLogger
    {
        private static bool _verbose = false;
        private static readonly StringBuilder _report = new StringBuilder();
        private static readonly List<string> _details = new List<string>();

        public static bool Verbose
        {
            get => _verbose;
            set => _verbose = value;
        }

        public static void Info(string msg)
        {
            Debug.Log($"[ATO] {msg}");
        }

        public static void Warn(string msg)
        {
            Debug.LogWarning($"[ATO] {msg}");
        }

        public static void Error(string msg)
        {
            Debug.LogError($"[ATO] {msg}");
        }

        /// <summary>详细日志（需 verboseLogging）。Verbose-only log.</summary>
        public static void VerboseLog(string msg)
        {
            if (_verbose) Debug.Log($"[ATO] {msg}");
        }

        /// <summary>计时的步骤日志。Timed step log.</summary>
        public static IDisposable Step(string msg)
        {
            return new StepScope(msg);
        }

        private sealed class StepScope : IDisposable
        {
            private readonly string _msg;
            private readonly Stopwatch _sw;
            public StepScope(string msg) { _msg = msg; _sw = Stopwatch.StartNew(); }
            public void Dispose()
            {
                _sw.Stop();
                Info($"{_msg} — {_sw.ElapsedMilliseconds} ms");
            }
        }

        // ---- 进度与取消 / progress & cancellation ----

        private static volatile bool _cancelled = false;
        private static string _currentStage = "";
        private static float _currentProgress = 0f;

        public static bool Cancelled => _cancelled;

        public static void Begin(string stage)
        {
            _cancelled = false;
            _currentStage = stage;
            _currentProgress = 0f;
            UpdateBar();
        }

        /// <summary>更新进度；若用户取消返回 false。Update progress; returns false if cancelled.</summary>
        public static bool Report(float progress01, string detail = "")
        {
            _currentProgress = Mathf.Clamp01(progress01);
            var cancelled = EditorUtility.DisplayCancelableProgressBar(
                $"[ATO] {ATOLocalization.Tr(_currentStage)}",
                detail ?? "",
                _currentProgress);
            if (cancelled) _cancelled = true;
            return !_cancelled;
        }

        private static void UpdateBar()
        {
            EditorUtility.DisplayCancelableProgressBar($"[ATO] {ATOLocalization.Tr(_currentStage)}", "", 0f);
        }

        public static void EndProgress()
        {
            EditorUtility.ClearProgressBar();
        }

        /// <summary>取消时终止烘焙（保留磁盘临时资产，释放 CPU/GPU/内存资源）。</summary>
        public static void ThrowIfCancelled()
        {
            if (_cancelled) throw new OperationCanceledException("[ATO] Cancelled by user");
        }

        // ---- 报告 / report ----

        public static void ReportLine(string line)
        {
            _report.AppendLine(line);
        }

        public static void ReportDetail(string line)
        {
            _details.Add(line);
        }

        public static void ResetReport()
        {
            _report.Clear();
            _details.Clear();
        }

        /// <summary>在 NDMF 控制台输出最终报告：默认展示总体结果，细节折叠。</summary>
        public static void FlushReport()
        {
            if (_report.Length == 0) return;
            var sb = new StringBuilder();
            sb.AppendLine("[ATO] ===== Summary =====");
            sb.Append(_report);
            if (_details.Count > 0)
            {
                sb.AppendLine("[ATO] ===== Details (expand) =====");
                foreach (var d in _details) sb.AppendLine("[ATO]   " + d);
            }
            Debug.Log(sb.ToString());
        }
    }
}
