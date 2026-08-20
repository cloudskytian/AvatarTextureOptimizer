// AvatarTextureOptimizer - AtoLog
// EN: Static logger with [ATO] prefix, stage timing and verbosity switch.
// CN: 带 [ATO] 前缀的静态日志，支持阶段计时与详细度开关。
using System;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// EN: Central logger. All output carries the [ATO] prefix so users can filter the console.
    /// CN: 统一日志入口。所有输出带 [ATO] 前缀，方便用户在控制台过滤。
    /// </summary>
    public static class AtoLog
    {
        // EN: Global verbosity; turned on from the component. Detail lines can be collapsed later.
        // CN: 全局详细度开关，由组件设置。详细行在报告中折叠展示。
        public static bool Detailed { get; set; } = true;

        // EN: Collects detailed log lines so the final report can show them collapsed.
        // CN: 收集详细日志行，最终报告折叠展示。
        private static readonly StringBuilder DetailBuffer = new StringBuilder(4096);

        public static void Info(string msg)
        {
            UnityEngine.Debug.Log("[ATO] " + msg);
        }

        public static void Warn(string msg)
        {
            UnityEngine.Debug.LogWarning("[ATO] " + msg);
        }

        public static void Error(string msg)
        {
            UnityEngine.Debug.LogError("[ATO] " + msg);
        }

        /// <summary>EN: Detailed line, only printed when Detailed is on. / CN: 详细行，仅在 Detailed 开启时输出。</summary>
        public static void Detail(string msg)
        {
            if (!Detailed) return;
            string line = "[ATO][detail] " + msg;
            DetailBuffer.AppendLine(line);
            UnityEngine.Debug.Log(line);
        }

        /// <summary>EN: Flushes buffered details into one log message (used at report time). / CN: 将缓冲的细节刷成一条日志。</summary>
        public static void FlushDetails(string title)
        {
            if (DetailBuffer.Length == 0) return;
            UnityEngine.Debug.Log($"[ATO] {title}\n{DetailBuffer}");
            DetailBuffer.Clear();
        }

        // EN: Measures a stage. Usage: using (var t = AtoLog.Time("Scan")) { ... }
        // CN: 阶段计时。用法: using (var t = AtoLog.Time("Scan")) { ... }
        public static TimeScope Time(string stage)
        {
            return new TimeScope(stage);
        }

        public struct TimeScope : IDisposable
        {
            private readonly string _stage;
            private readonly Stopwatch _sw;
            private readonly bool _detail;

            public TimeScope(string stage)
            {
                _stage = stage;
                _detail = Detailed;
                _sw = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                _sw.Stop();
                double ms = _sw.Elapsed.TotalMilliseconds;
                string line = $"{_stage}: {ms:F1} ms";
                if (_detail) Detail(line); else Info(line);
            }
        }

        /// <summary>EN: Formats a byte size human-readably. / CN: 人类可读字节数。</summary>
        public static string Bytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        /// <summary>EN: Percent helper. / CN: 百分比助手。</summary>
        public static string Pct(float v) => $"{v * 100.0f:F1}%";
    }

    /// <summary>
    /// EN: Exception thrown to abort the ATO bake with a user-facing message.
    /// CN: 用于中止 ATO 烘焙并向用户展示消息的异常。
    /// </summary>
    public class AtoAbortException : Exception
    {
        public AtoAbortException(string message) : base(message) { }
    }
}
