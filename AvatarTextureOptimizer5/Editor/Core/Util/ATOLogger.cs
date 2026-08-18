// Copyright (c) fosa. Licensed under the MIT License.
// Structured logging and timing. Every message is prefixed with [ATO] and can be toggled off.
// 结构化日志与计时。所有消息以 [ATO] 前缀输出，并可通过开关关闭。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Debug = UnityEngine.Debug;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// One recorded stage of the pipeline, used for both the console log and the final report.
    /// 管线中记录下来的一个阶段，同时用于控制台日志与最终报告。
    /// </summary>
    public sealed class StageRecord
    {
        /// <summary>Stage name. / 阶段名称。</summary>
        public string Name;

        /// <summary>Elapsed wall-clock milliseconds. / 实际耗时（毫秒）。</summary>
        public double Milliseconds;

        /// <summary>Free-form detail lines shown folded in the report. / 报告中折叠显示的自由格式明细。</summary>
        public readonly List<string> Details = new List<string>();
    }

    /// <summary>
    /// Central logger. Verbose output is opt-in so normal users only see the summary, while
    /// advanced users can switch on a full trace without recompiling.
    /// 中央日志器。详细输出为可选项，普通用户只看到摘要，高级用户无需重新编译即可开启完整追踪。
    /// </summary>
    public sealed class ATOLogger
    {
        private const string Prefix = "[ATO] ";

        private readonly List<StageRecord> _stages = new List<StageRecord>();
        private readonly List<string> _warnings = new List<string>();
        private readonly List<string> _errors = new List<string>();
        private StageRecord _current;

        /// <summary>When false only warnings and errors reach the console. / 为 false 时仅警告与错误输出到控制台。</summary>
        public bool Verbose { get; set; }

        /// <summary>All recorded stages in execution order. / 按执行顺序记录的所有阶段。</summary>
        public IReadOnlyList<StageRecord> Stages => _stages;

        /// <summary>All warnings raised during the build. / 构建期间产生的所有警告。</summary>
        public IReadOnlyList<string> Warnings => _warnings;

        /// <summary>All errors raised during the build. / 构建期间产生的所有错误。</summary>
        public IReadOnlyList<string> Errors => _errors;

        /// <summary>Total pipeline time in milliseconds. / 管线总耗时（毫秒）。</summary>
        public double TotalMilliseconds
        {
            get
            {
                double sum = 0;
                foreach (var s in _stages) sum += s.Milliseconds;
                return sum;
            }
        }

        /// <summary>Writes an informational line when verbose logging is on. / 详细日志开启时写入一条信息。</summary>
        public void Info(string message)
        {
            if (Verbose) Debug.Log(Prefix + message);
        }

        /// <summary>
        /// Records a detail line against the current stage, and echoes it when verbose.
        /// 在当前阶段记录一条明细，详细模式下同时输出到控制台。
        /// </summary>
        public void Detail(string message)
        {
            _current?.Details.Add(message);
            if (Verbose) Debug.Log(Prefix + message);
        }

        /// <summary>Records and prints a warning. Always shown. / 记录并打印警告，始终显示。</summary>
        public void Warning(string message)
        {
            _warnings.Add(message);
            _current?.Details.Add("WARN: " + message);
            Debug.LogWarning(Prefix + message);
        }

        /// <summary>Records and prints an error. Always shown. / 记录并打印错误，始终显示。</summary>
        public void Error(string message)
        {
            _errors.Add(message);
            _current?.Details.Add("ERROR: " + message);
            Debug.LogError(Prefix + message);
        }

        /// <summary>
        /// Opens a timed stage. Dispose the returned scope to close it.
        /// 开启一个计时阶段，释放返回的作用域即结束计时。
        /// </summary>
        public StageScope Stage(string name)
        {
            var record = new StageRecord { Name = name };
            _stages.Add(record);
            _current = record;
            return new StageScope(this, record);
        }

        private void CloseStage(StageRecord record, double ms)
        {
            record.Milliseconds = ms;
            if (ReferenceEquals(_current, record)) _current = null;
            if (Verbose)
            {
                Debug.Log(Prefix + string.Format(CultureInfo.InvariantCulture,
                    "Stage '{0}' finished in {1:F1} ms", record.Name, ms));
            }
        }

        /// <summary>
        /// A disposable timing scope produced by <see cref="Stage" />.
        /// 由 <see cref="Stage" /> 产生的可释放计时作用域。
        /// </summary>
        public readonly struct StageScope : IDisposable
        {
            private readonly ATOLogger _owner;
            private readonly StageRecord _record;
            private readonly Stopwatch _sw;

            internal StageScope(ATOLogger owner, StageRecord record)
            {
                _owner = owner;
                _record = record;
                _sw = Stopwatch.StartNew();
            }

            /// <summary>Closes the stage and records elapsed time. / 结束阶段并记录耗时。</summary>
            public void Dispose()
            {
                _sw.Stop();
                _owner.CloseStage(_record, _sw.Elapsed.TotalMilliseconds);
            }
        }

        /// <summary>
        /// Builds the human-readable report shown in the NDMF console: a one-line summary
        /// followed by folded per-stage detail.
        /// 构建 NDMF 控制台中显示的可读报告：单行摘要 + 折叠的分阶段明细。
        /// </summary>
        public string BuildReport(string summaryLine)
        {
            var sb = new StringBuilder();
            sb.Append(summaryLine);
            sb.AppendLine();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "Total time: {0:F1} ms, warnings: {1}, errors: {2}",
                TotalMilliseconds, _warnings.Count, _errors.Count));

            foreach (var stage in _stages)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  - {0}: {1:F1} ms", stage.Name, stage.Milliseconds));
                foreach (var d in stage.Details)
                {
                    sb.Append("      ").AppendLine(d);
                }
            }

            return sb.ToString();
        }
    }
}
