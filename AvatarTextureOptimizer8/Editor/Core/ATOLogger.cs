// ATOLogger.cs
// Central [ATO] logger with verbosity switch and stage timers.
// 带verbosity开关与阶段计时器的 [ATO] 日志器。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Debug = UnityEngine.Debug;

namespace net.fosa.ato
{
    /// <summary>Log verbosity levels. / 日志级别。</summary>
    internal enum ATOLogLevel
    {
        /// <summary>Always logged: warnings, errors, final report. / 始终输出:警告、错误、最终报告。</summary>
        Info = 0,
        /// <summary>Logged when verbose switch is on. / verbose 开启时输出。</summary>
        Verbose = 1,
    }

    internal static class ATOLog
    {
        /// <summary>Verbose switch, set from component settings at pass start. / 详细日志开关,Pass 开始时从组件设置读取。</summary>
        internal static bool Verbose;

        private static readonly List<StageTiming> StageTimings = new List<StageTiming>();

        /// <summary>One measured stage. / 一条计时记录。</summary>
        internal sealed class StageTiming
        {
            internal string Stage; internal double Ms;
            internal StageTiming(string stage, double ms) { Stage = stage; Ms = ms; }
        }

        internal static void EnableVerbose(bool on) => Verbose = on;

        internal static void Info(string message) => Debug.Log($"[ATO] {message}");
        internal static void Warn(string message) => Debug.LogWarning($"[ATO] {message}");
        internal static void Error(string message) => Debug.LogError($"[ATO] {message}");

        /// <summary>Verbose-only log. / 仅 verbose 模式输出。</summary>
        internal static void V(string message)
        {
            if (Verbose) Debug.Log($"[ATO] {message}");
        }

        /// <summary>Verbose log with an object context for the NDMF console. / 带对象上下文的 verbose 日志。</summary>
        internal static void V(object context, string message)
        {
            if (Verbose) Debug.Log($"[ATO] {message}", context as UnityEngine.Object);
        }

        // ------------------------------------------------------------------ //
        // Stage timing / 阶段计时
        // ------------------------------------------------------------------ //
        internal static IDisposable Stage(string name)
        {
            var token = new StageToken(name);
            ATOLog.V($"---- stage start: {name} ----");
            return token;
        }
        private static void EndStage(StageToken t)
        {
            var ms = t.Watch.Elapsed.TotalMilliseconds;
            StageTimings.Add(new StageTiming(t.Name, ms));
            ATOLog.V($"---- stage end: {t.Name} ({ms:F1} ms) ----");
        }

        private sealed class StageToken : IDisposable
        {
            internal readonly string Name;
            internal readonly Stopwatch Watch = Stopwatch.StartNew();
            internal StageToken(string name) => Name = name;
            public void Dispose()
            {
                Watch.Stop();
                EndStage(this);
            }
        }

        /// <summary>Reset collected timings (per avatar build). / 重置计时集合(每次构建)。</summary>
        internal static void ResetTimings()
        {
            StageTimings.Clear();
        }

        /// <summary>Render collected timings as text. / 渲染计时文本。</summary>
        internal static string RenderTimings()
        {
            var sb = new StringBuilder();
            double total = 0;
            foreach (var e in StageTimings) total += e.Ms;
            sb.AppendLine($"total: {total / 1000.0:F3} s");
            foreach (var e in StageTimings)
                sb.AppendLine($"{e.Stage}: {e.Ms / 1000.0:F3} s ({(total > 0 ? e.Ms * 100.0 / total : 0):F1}%)");
            return sb.ToString();
        }

        internal static IReadOnlyList<StageTiming> Timings => StageTimings;
    }
}
