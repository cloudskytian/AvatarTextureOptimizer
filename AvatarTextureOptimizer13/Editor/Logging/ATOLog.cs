// ATO — Avatar Texture Optimizer
// Central logging with the [ATO] prefix, verbosity switch and per-stage timers.
// 带 [ATO] 前缀、详细度开关与分阶段计时的集中式日志。
//
// Every log line carries the [ATO] prefix so users can filter the console easily.
// A verbosity flag is available now (tool is in development) and can be exposed to
// advanced users later without changing call sites.
// 每条日志都带 [ATO] 前缀，方便在控制台过滤。详细度开关当前即可用（工具开发阶段），
// 未来开放给高级用户时无需改动调用点。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// [ATO] logging utilities. [ATO] 日志工具。
    /// </summary>
    public static class ATOLog
    {
        /// <summary>Global verbosity flag (mirrors the component setting during a build). 全局详细度开关（构建时镜像组件设置）。</summary>
        public static bool Verbose { get; set; } = true;

        private const string Prefix = "[ATO]";
        private const string VerbosePrefix = "[ATO][V]";

        public static void Info(string msg) => Debug.Log($"{Prefix} {msg}");
        public static void Warn(string msg) => Debug.LogWarning($"{Prefix} {msg}");
        public static void Error(string msg) => Debug.LogError($"{Prefix} {msg}");
        public static void Verbose(string msg)
        {
            if (Verbose) Debug.Log($"{VerbosePrefix} {msg}");
        }

        /// <summary>
        /// Simple stopwatch wrapper used to time pipeline stages. 用于给管线各阶段计时的秒表封装。
        /// </summary>
        public sealed class StageTimer
        {
            private readonly Stopwatch _sw = new Stopwatch();
            private readonly string _name;

            public StageTimer(string name)
            {
                _name = name;
                _sw.Start();
            }

            /// <summary>Finish the stage, log its duration and return elapsed ms. 结束阶段、记录耗时并返回毫秒数。</summary>
            public double Stop()
            {
                _sw.Stop();
                var ms = _sw.Elapsed.TotalMilliseconds;
                ATOLog.Verbose($"{_name}: {ms:F1} ms");
                return ms;
            }

            public double ElapsedMs => _sw.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>
    /// Accumulates per-stage timings and the final report text. 累计各阶段耗时与最终报告文本。
    /// </summary>
    public class ATOReport
    {
        public readonly Dictionary<string, double> StageTimings = new Dictionary<string, double>();
        public readonly List<string> DetailLines = new List<string>();
        public readonly List<string> WarningLines = new List<string>();

        public int TexturesProcessed;
        public int AtlasesGenerated;
        public int IslandsProcessed;
        public long EstimatedBytesBefore;
        public long EstimatedBytesAfter;

        public void AddStage(string name, double ms) => StageTimings[name] = ms;

        public void AddDetail(string line)
        {
            DetailLines.Add(line);
            ATOLog.Verbose(line);
        }

        public void AddWarning(string line)
        {
            WarningLines.Add(line);
            ATOLog.Warn(line);
        }

        public double TotalMs
        {
            get
            {
                double t = 0;
                foreach (var v in StageTimings.Values) t += v;
                return t;
            }
        }
    }
}
