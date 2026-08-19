// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Logging & timing.
// AvatarTextureOptimizer (ATO) - 日志与耗时统计。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Debug = UnityEngine.Debug;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Core
{
    /// <summary>
    /// EN: Every ATO log line is prefixed with <c>[ATO]</c>. Verbose logging is opt-in per component so
    ///     advanced users can debug without recompiling; timings are always collected because the final
    ///     NDMF report needs them.
    /// ZH: 所有 ATO 日志都以 <c>[ATO]</c> 开头。详细日志由组件上的开关控制，方便高级用户无需重新编译即可调试；
    ///     耗时统计始终收集，因为最终的 NDMF 报告需要它。
    /// </summary>
    public static class ATOLog
    {
        public const string Prefix = "[ATO]";

        /// <summary>EN: Set from the component settings at the start of every build. ZH: 每次构建开始时由组件设置写入。</summary>
        public static bool Verbose { get; set; }

        /// <summary>EN: Per-island metric traces. Extremely noisy. ZH: 逐岛指标追踪，极其冗长。</summary>
        public static bool TraceIslands { get; set; }

        private static readonly List<TimingEntry> _timings = new List<TimingEntry>();
        private static readonly Stopwatch _wall = new Stopwatch();

        public readonly struct TimingEntry
        {
            public readonly string Stage;
            public readonly double Milliseconds;
            public readonly string Detail;

            public TimingEntry(string stage, double ms, string detail)
            {
                Stage = stage;
                Milliseconds = ms;
                Detail = detail;
            }
        }

        public static IReadOnlyList<TimingEntry> Timings => _timings;
        public static double TotalMilliseconds => _wall.Elapsed.TotalMilliseconds;

        public static void BeginBuild(bool verbose, bool traceIslands)
        {
            Verbose = verbose;
            TraceIslands = traceIslands;
            _timings.Clear();
            _wall.Reset();
            _wall.Start();
            Info("=== Avatar Texture Optimizer build started ===");
        }

        public static void EndBuild()
        {
            _wall.Stop();
            Info($"=== Avatar Texture Optimizer build finished in {_wall.Elapsed.TotalMilliseconds:F1} ms ===");
        }

        /// <summary>EN: Always printed. ZH: 始终输出。</summary>
        public static void Info(string message) => Debug.Log($"{Prefix} {message}");

        /// <summary>EN: Only printed when verbose logging is enabled. ZH: 仅在开启详细日志时输出。</summary>
        public static void Debug_(string message)
        {
            if (Verbose) Debug.Log($"{Prefix} {message}");
        }

        /// <summary>EN: Per-island trace. ZH: 逐岛追踪日志。</summary>
        public static void Trace(string message)
        {
            if (TraceIslands) Debug.Log($"{Prefix} [trace] {message}");
        }

        public static void Warn(string message) => Debug.LogWarning($"{Prefix} {message}");

        public static void Error(string message) => Debug.LogError($"{Prefix} {message}");

        public static void Exception(Exception e) => Debug.LogException(e);

        /// <summary>
        /// EN: Times a stage and records it for the final report. Use with <c>using</c>.
        /// ZH: 统计一个阶段的耗时并记录到最终报告。请配合 <c>using</c> 使用。
        /// </summary>
        public static StageScope Stage(string name, string detail = null) => new StageScope(name, detail);

        public readonly struct StageScope : IDisposable
        {
            private readonly Stopwatch _sw;
            private readonly string _name;
            private readonly string _detail;

            internal StageScope(string name, string detail)
            {
                _name = name;
                _detail = detail;
                _sw = Stopwatch.StartNew();
                Debug_($"> {name} ...");
            }

            public void Dispose()
            {
                _sw.Stop();
                _timings.Add(new TimingEntry(_name, _sw.Elapsed.TotalMilliseconds, _detail));
                Debug_($"< {_name} done in {_sw.Elapsed.TotalMilliseconds:F1} ms");
            }
        }

        /// <summary>EN: Render the timing table for the report. ZH: 生成报告用的耗时表格。</summary>
        public static string FormatTimings()
        {
            var sb = new StringBuilder();
            foreach (var t in _timings)
            {
                sb.Append("  ").Append(t.Stage.PadRight(38)).Append(t.Milliseconds.ToString("F1").PadLeft(9))
                    .Append(" ms");
                if (!string.IsNullOrEmpty(t.Detail)) sb.Append("   ").Append(t.Detail);
                sb.Append('\n');
            }
            sb.Append("  ").Append("TOTAL".PadRight(38)).Append(TotalMilliseconds.ToString("F1").PadLeft(9))
                .Append(" ms\n");
            return sb.ToString();
        }
    }
}
