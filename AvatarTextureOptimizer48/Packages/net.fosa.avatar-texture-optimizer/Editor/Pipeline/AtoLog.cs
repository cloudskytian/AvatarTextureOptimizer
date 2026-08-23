// [ATO] logging utility. / [ATO] 日志工具。
// All logs are prefixed with [ATO] and can be silenced with the component's verboseLogs flag.
// / 所有日志以 [ATO] 开头，可通过组件上的 verboseLogs 开关控制详细程度。

using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.pipeline
{
    /// <summary>
    /// Centralized logging. / 统一日志入口。
    /// </summary>
    public static class AtoLog
    {
        /// <summary>Global verbose flag, set from the component before running. / 全局详细日志开关。</summary>
        public static bool Verbose;

        public static void Info(string msg)
        {
            Debug.Log("[ATO] " + msg);
        }

        public static void VerboseLog(string msg)
        {
            if (Verbose) Debug.Log("[ATO][V] " + msg);
        }

        public static void Warn(string msg)
        {
            Debug.LogWarning("[ATO] " + msg);
        }

        public static void Error(string msg)
        {
            Debug.LogError("[ATO] " + msg);
        }
    }
}
