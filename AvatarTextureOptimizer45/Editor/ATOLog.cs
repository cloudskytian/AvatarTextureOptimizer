using System;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// ATO 日志系统 / ATO logging.
    /// 所有日志以 [ATO] 开头, 详细日志受组件上的 debugLogging 开关控制(预留高级用户调试用).
    /// All messages are prefixed with [ATO]; verbose messages are gated by the component's
    /// debugLogging toggle (reserved for advanced users).
    /// </summary>
    internal static class ATOLog
    {
        public const string Prefix = "[ATO]";

        /// <summary>当前是否输出详细日志 / Whether verbose logging is currently enabled.</summary>
        public static bool Verbose;

        public static void Info(string msg)
        {
            Debug.Log($"{Prefix} {msg}");
        }

        public static void InfoVerbose(string msg)
        {
            if (Verbose) Debug.Log($"{Prefix} [D] {msg}");
        }

        public static void Warn(string msg)
        {
            Debug.LogWarning($"{Prefix} {msg}");
        }

        public static void Error(string msg)
        {
            Debug.LogError($"{Prefix} {msg}");
        }

        /// <summary>
        /// 计时器: 记录一个阶段的总耗时与各子步骤耗时 / Timer for a stage with sub-step timings.
        /// </summary>
        public sealed class StageTimer
        {
            private readonly Stopwatch _total = new Stopwatch();
            private readonly StringBuilder _steps = new StringBuilder();
            private Stopwatch _current;
            private string _currentName;

            public void Start()
            {
                _total.Start();
            }

            public void BeginStep(string name)
            {
                _currentName = name;
                _current = Stopwatch.StartNew();
            }

            public void EndStep()
            {
                if (_current == null) return;
                _current.Stop();
                if (_steps.Length > 0) _steps.Append(", ");
                _steps.Append(_currentName).Append('=').Append(_current.ElapsedMilliseconds).Append("ms");
                _current = null;
            }

            /// <summary>结束整个阶段并输出汇总 / Ends the stage and logs the summary.</summary>
            public void End(string stageName)
            {
                _total.Stop();
                Info($"{stageName} 完成, 总耗时 {_total.ElapsedMilliseconds}ms | steps: {_steps}");
            }
        }
    }
}
