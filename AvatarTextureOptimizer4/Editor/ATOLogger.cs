// Avatar Texture Optimizer (ATO)
// Central logging helper. All messages are prefixed with [ATO].
// 集中日志助手。所有信息以 [ATO] 为前缀。
//
// Two levels: debug (always useful for development) and verbose (per-island detail).
// 两级日志：debug（开发期始终有用）与 verbose（逐岛细节）。

using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Central logger for ATO. / ATO 集中日志器。
    /// </summary>
    public static class ATOLogger
    {
        private static bool _debugEnabled = true;
        private static bool _verboseEnabled = false;

        /// <summary>Configure the logger from advanced settings. / 依据高级设置配置日志器。</summary>
        public static void Configure(bool debugEnabled, bool verboseEnabled)
        {
            _debugEnabled = debugEnabled;
            _verboseEnabled = verboseEnabled;
        }

        public static bool DebugEnabled => _debugEnabled;
        public static bool VerboseEnabled => _verboseEnabled;

        public static void Info(string message)
        {
            Debug.Log(ATOConstants.LogPrefix + " " + message);
        }

        public static void Debug(string message)
        {
            if (_debugEnabled) Debug.Log(ATOConstants.LogPrefix + " [debug] " + message);
        }

        public static void Verbose(string message)
        {
            if (_verboseEnabled) Debug.Log(ATOConstants.LogPrefix + " [verbose] " + message);
        }

        public static void Warn(string message)
        {
            Debug.LogWarning(ATOConstants.LogPrefix + " " + message);
        }

        public static void Error(string message)
        {
            Debug.LogError(ATOConstants.LogPrefix + " " + message);
        }

        /// <summary>
        /// Log a single step with its elapsed time. / 记录单步耗时。
        /// </summary>
        public static void Step(string name, double elapsedMs)
        {
            Debug(StepMessage(name, elapsedMs));
        }

        public static string StepMessage(string name, double elapsedMs)
        {
            return $"Step '{name}' took {elapsedMs:F1} ms";
        }
    }
}
