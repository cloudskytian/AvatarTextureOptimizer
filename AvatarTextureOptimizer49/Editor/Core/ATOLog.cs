using System;
using System.Collections.Generic;
using System.Diagnostics;
using Debug = UnityEngine.Debug;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>Thrown when the user cancels the build via the progress bar. / 用户通过进度条取消时抛出。</summary>
    internal class AtoCancelledException : OperationCanceledException
    {
        public AtoCancelledException() : base("ATO build cancelled by user / ATO 构建已被用户取消") { }
    }

    /// <summary>Thrown to abort the whole build after reporting a fatal error. / 报告致命错误后中止整个构建时抛出。</summary>
    internal class AtoAbortException : Exception
    {
        public AtoAbortException(string message) : base(message) { }
    }

    /// <summary>
    /// [ATO] logger with per-stage timing. Enabled toggles are set by the processor from user settings.
    /// / 带 [ATO] 前缀与分阶段计时的日志器；开关由处理器按用户设置注入。
    /// </summary>
    internal static class ATOLog
    {
        /// <summary>Always-on basic logging (important milestones only). / 常开的基础日志（仅关键里程碑）。</summary>
        internal static bool InfoEnabled = true;
        /// <summary>Verbose logging for debugging. / 调试用详细日志。</summary>
        internal static bool VerboseEnabled = false;

        /// <summary>Stage timings collected for the final report. / 供最终报告使用的阶段耗时。</summary>
        internal static readonly List<(string stage, double ms)> StageTimings = new List<(string, double)>();

        internal static void Info(string message)
        {
            if (InfoEnabled) Debug.Log("[ATO] " + message);
        }

        internal static void Verbose(string message)
        {
            if (VerboseEnabled) Debug.Log("[ATO][V] " + message);
        }

        internal static void Warning(string message)
        {
            Debug.LogWarning("[ATO] " + message);
        }

        internal static void Error(string message)
        {
            Debug.LogError("[ATO] " + message);
        }

        internal static void ResetTimings() => StageTimings.Clear();

        /// <summary>Scoped stage timer: logs elapsed ms and records it for the report. / 阶段计时作用域：记录耗时并输出。</summary>
        internal static StageScope Stage(string name) => new StageScope(name);

        internal readonly struct StageScope : IDisposable
        {
            private readonly string _name;
            private readonly Stopwatch _sw;

            internal StageScope(string name)
            {
                _name = name;
                _sw = Stopwatch.StartNew();
                if (VerboseEnabled) Debug.Log($"[ATO][V] → stage start: {name}");
            }

            public void Dispose()
            {
                _sw.Stop();
                StageTimings.Add((_name, _sw.Elapsed.TotalMilliseconds));
                Debug.Log($"[ATO] stage '{_name}' took {_sw.Elapsed.TotalMilliseconds:F1} ms");
            }
        }
    }
}
