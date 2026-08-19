// AvatarTextureOptimizer
// File: Editor/Logging/ATOLog.cs
//
// Unified logging for the tool. Every message is prefixed with [ATO] so users
// can filter the Unity console. Levels allow the verbose switch to be toggled
// at runtime for debugging, and timings are captured per phase.
//
// 工具的统一日志。每条消息都带 [ATO] 前缀，便于用户在 Unity 控制台过滤。
// 分级日志允许随时开关详细输出用于调试，并按阶段记录耗时。

using System;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.logging
{
    public enum ATOLogLevel
    {
        Trace = 0,  // 详细内部日志（默认关闭）/ verbose internal logs (off by default)
        Info = 1,   // 常规信息 / normal info
        Warn = 2,   // 警告 / warnings
        Error = 3,  // 错误 / errors
    }

    /// <summary>
    /// Central log sink. Supports optional runtime toggling of verbose output
    /// so advanced users can debug without editing code.
    /// 中央日志输出。支持运行期开关详细输出，高级用户无需改代码即可调试。
    /// </summary>
    public static class ATOLog
    {
        private const string Prefix = "[ATO] ";

        /// <summary>Global verbose switch (mirrors the component setting). / 全局详细开关（与组件设置同步）。</summary>
        public static bool Verbose { get; set; }

        /// <summary>Optional sink override (e.g. the progress window). / 可选的输出重定向（如进度窗口）。</summary>
        public static Action<string, ATOLogLevel> Sink;

        public static void Trace(string msg)
        {
            if (!Verbose) return;
            Write(msg, ATOLogLevel.Trace);
        }

        public static void Info(string msg) => Write(msg, ATOLogLevel.Info);
        public static void Warn(string msg) => Write(msg, ATOLogLevel.Warn);
        public static void Error(string msg) => Write(msg, ATOLogLevel.Error);

        public static void Exception(Exception e)
        {
            Debug.LogException(e);
        }

        private static void Write(string msg, ATOLogLevel level)
        {
            var line = Prefix + msg;
            try
            {
                Sink?.Invoke(line, level);
            }
            catch
            {
                // A broken sink must never break the build.
                // 输出重定向损坏绝不能中断构建。
            }
            switch (level)
            {
                case ATOLogLevel.Trace:
                case ATOLogLevel.Info: Debug.Log(line); break;
                case ATOLogLevel.Warn: Debug.LogWarning(line); break;
                case ATOLogLevel.Error: Debug.LogError(line); break;
            }
        }
    }

    /// <summary>
    /// Simple hierarchical stopwatch for reporting per-phase timings.
    /// 用于报告各阶段耗时的简易分层计时器。
    /// </summary>
    public sealed class ATOStopwatch
    {
        private readonly Stopwatch _sw = new Stopwatch();
        private readonly StringBuilder _sb = new StringBuilder();

        public ATOStopwatch(string phase)
        {
            _sb.AppendLine($"[ATO] === {phase} ===");
        }

        /// <summary>Begin timing a sub-phase. / 开始计时一个子阶段。</summary>
        public void Begin(string name)
        {
            _sw.Restart();
            ATOLog.Trace($"begin: {name}");
        }

        /// <summary>Stop timing and record the duration. / 停止计时并记录耗时。</summary>
        public void End(string name)
        {
            _sw.Stop();
            _sb.AppendLine($"[ATO]   {name}: {_sw.ElapsedMilliseconds} ms");
            ATOLog.Trace($"end: {name} ({_sw.ElapsedMilliseconds} ms)");
        }

        public override string ToString() => _sb.ToString();
    }
}
