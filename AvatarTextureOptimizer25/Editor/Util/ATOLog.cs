// Avatar Texture Optimizer / 头像贴图优化器
// Central logging helper. All lines are prefixed with [ATO].
// 统一日志工具。所有日志行都以 [ATO] 开头。
//
// The verbose switch lives on the component (AvatarTextureOptimizer.verboseLogging)
// and is mirrored here at pipeline start. Summary/user facing info always logs via
// Info(); step details only when VerboseEnabled.
// 详细日志开关在组件上（AvatarTextureOptimizer.verboseLogging），管线开始时同步到此处。
// 面向用户的汇总信息始终用 Info()；步骤细节仅在 VerboseEnabled 时输出。

using System;
using System.Diagnostics;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Central [ATO] logger. / 统一 [ATO] 日志器。</summary>
    public static class ATOLog
    {
        /// <summary>The required [ATO] prefix on every line. / 每行必带的 [ATO] 前缀。</summary>
        public const string Prefix = "[ATO] ";

        /// <summary>Verbose logging switch, set at pipeline start. / 详细日志开关（管线开始时设置）。</summary>
        public static bool VerboseEnabled { get; set; }

        /// <summary>Always-on informational log. / 始终输出的信息日志。</summary>
        public static void Info(string message)
        {
            UnityEngine.Debug.Log(Prefix + message);
        }

        /// <summary>Verbose step log (also used for debugging). / 详细步骤日志（调试用）。</summary>
        public static void Verbose(string message)
        {
            if (VerboseEnabled) UnityEngine.Debug.Log(Prefix + message);
        }

        /// <summary>Warning log. / 警告日志。</summary>
        public static void Warn(string message)
        {
            UnityEngine.Debug.LogWarning(Prefix + message);
        }

        /// <summary>Error log. / 错误日志。</summary>
        public static void Error(string message)
        {
            UnityEngine.Debug.LogError(Prefix + message);
        }

        /// <summary>
        /// Measures a pipeline step; logs elapsed time when disposed (verbose only).
        /// 统计某一步耗时；Dispose 时输出（仅详细模式）。
        /// </summary>
        public sealed class Step : IDisposable
        {
            private readonly string _name;
            private readonly Stopwatch _sw = Stopwatch.StartNew();

            public Step(string name)
            {
                _name = name;
                Verbose($"[{_name}] start / 开始");
            }

            /// <summary>Elapsed milliseconds so far. / 目前已耗时（毫秒）。</summary>
            public long ElapsedMs => _sw.ElapsedMilliseconds;

            public void Dispose()
            {
                _sw.Stop();
                Verbose($"[{_name}] done in {_sw.ElapsedMilliseconds} ms / 用时 {_sw.ElapsedMilliseconds} ms");
            }
        }
    }
}
