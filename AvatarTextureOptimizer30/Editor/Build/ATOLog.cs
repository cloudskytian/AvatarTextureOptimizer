// ATOLog.cs — 日志系统（[ATO] 前缀、耗时统计、详细度开关）/ Logging system ([ATO] prefix, timing, verbosity switch).
// 说明：所有日志均带 [ATO] 前缀；每阶段耗时必记；详细度由 ATOLogVerbosity 控制（默认 Normal）。
// Note: all logs use the [ATO] prefix; per-stage timings are always recorded; verbosity is controlled by ATOLogVerbosity.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>ATO 日志器。/ ATO logger.</summary>
    public static class ATOLog
    {
        // 详细度（由组件配置在构建开始时设置）/ verbosity (set from the component config at build start)
        public static ATOLogVerbosity Verbosity = ATOLogVerbosity.Normal;

        // 静态开关：关闭后所有日志静默（构建流程内部控制）/ master switch: silences all logs (controlled internally during build)
        public static bool Enabled = true;

        private const string Prefix = "[ATO]";

        /// <summary>信息日志（Normal 级）。/ Info log (Normal level).</summary>
        public static void Info(string msg)
        {
            if (Enabled && Verbosity >= ATOLogVerbosity.Normal) Debug.Log(Prefix + " " + msg);
        }

        /// <summary>详细日志（Verbose 级）。/ Verbose log (Verbose level).</summary>
        public static void Verbose(string msg)
        {
            if (Enabled && Verbosity >= ATOLogVerbosity.Verbose) Debug.Log(Prefix + " " + msg);
        }

        /// <summary>警告日志（任何级别都显示）。/ Warning log (always shown).</summary>
        public static void Warning(string msg)
        {
            if (Enabled) Debug.LogWarning(Prefix + " " + msg);
        }

        /// <summary>错误日志（任何级别都显示）。/ Error log (always shown).</summary>
        public static void Error(string msg)
        {
            if (Enabled) Debug.LogError(Prefix + " " + msg);
        }

        /// <summary>阶段计时器：记录每步耗时并输出日志。构建完成时统一汇总到控制台报告。/ Stage timer: per-step durations, summarized in the console report.</summary>
        public sealed class StageTimer
        {
            private readonly string _name;               // 阶段名 / stage name
            private readonly Stopwatch _sw = new Stopwatch();
            private string _detail;                      // 附加细节 / extra detail
            public double ElapsedMs => _sw.Elapsed.TotalMilliseconds;
            public string Name => _name;
            public string DetailText => _detail;

            public StageTimer(string name)
            {
                _name = name;
                _sw.Start();
            }

            /// <summary>设置附加细节（如处理数量）。/ Set extra detail (e.g. processed counts).</summary>
            public StageTimer Detail(string detail)
            {
                _detail = detail;
                return this;
            }

            /// <summary>停止计时、记录到全局注册表并输出日志。/ Stop, record to the global registry and log.</summary>
            public void Stop()
            {
                _sw.Stop();
                Stages.Add((_name, ElapsedMs, _detail));
                var msg = $"{_name}: {ElapsedMs:F1} ms";
                if (!string.IsNullOrEmpty(_detail)) msg += $" ({_detail})";
                Info(msg);
            }
        }

        /// <summary>全部阶段耗时注册表（报告用）。/ Global stage-timing registry (for the report).</summary>
        public static readonly List<(string name, double ms, string detail)> Stages = new List<(string, double, string)>();

        /// <summary>清空阶段注册表（每次构建前）。/ Clear the registry (before each build).</summary>
        public static void ClearStages() => Stages.Clear();

        /// <summary>格式化字节数为人类可读文本。/ Format bytes into human-readable text.</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024.0)).ToString("F2") + " MB";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("F3") + " GB";
        }

        /// <summary>百分比格式化。/ Percentage formatting.</summary>
        public static string FormatPct(double v) => (v * 100.0).ToString("F1") + "%";
    }

    /// <summary>
    /// 构建阶段进度与取消控制：显示进度条、支持取消；取消时终止构建、释放资源并保留磁盘临时资产。
    /// Build progress & cancellation: shows a progress bar and supports cancel; on cancel the build aborts,
    /// resources are released and temp assets remain on disk.
    /// </summary>
    public static class ATOProgress
    {
        private static volatile bool _cancelRequested;
        private static string _stage = "";
        private static int _lastReportedPct = -1;

        /// <summary>是否已请求取消。/ Whether cancellation has been requested.</summary>
        public static bool CancelRequested => _cancelRequested;

        /// <summary>请求取消（由 UI 或快捷键触发）。/ Request cancellation (from UI or shortcut).</summary>
        public static void RequestCancel()
        {
            _cancelRequested = true;
            ATOLog.Warning("Build cancellation requested by user. (用户请求取消构建)");
        }

        /// <summary>重置状态（构建开始时调用）。/ Reset state (called at build start).</summary>
        public static void Reset(string stage)
        {
            _cancelRequested = false;
            _lastReportedPct = -1;
            _stage = stage;
        }

        /// <summary>
        /// 更新进度（0~1）。返回是否应中止（已取消或 Unity 自身进度条被用户取消）。
        /// Update progress (0~1). Returns whether to abort (cancelled by user or Unity's own progress bar).
        /// </summary>
        public static bool Update(float progress01, string detail)
        {
            if (_cancelRequested) return true;
            if (UnityEditor.EditorUtility.DisplayCancelableProgressBar(
                    "ATO: Avatar Texture Optimizer - " + _stage,
                    detail,
                    Mathf.Clamp01(progress01)))
            {
                RequestCancel();
                return true;
            }
            return false;
        }

        /// <summary>关闭进度条。/ Clear the progress bar.</summary>
        public static void Clear()
        {
            UnityEditor.EditorUtility.ClearProgressBar();
            _lastReportedPct = -1;
        }

        /// <summary>取消时抛出的异常（被 NDMF 捕获后构建中止）。/ Exception thrown on cancel (caught by NDMF to abort the build).</summary>
        public sealed class BuildCancelledException : Exception
        {
            public BuildCancelledException() : base("ATO build cancelled by user. (用户取消了 ATO 构建)") { }
        }

        /// <summary>取消检查：若已取消则抛出。/ Cancellation check: throws if cancelled.</summary>
        public static void ThrowIfCancelled()
        {
            if (_cancelRequested) throw new BuildCancelledException();
        }

        /// <summary>拼接阶段文本。/ Build stage label.</summary>
        public static void SetStage(string stage)
        {
            _stage = stage;
        }
    }
}
