// -----------------------------------------------------------------------------
// ATOLog.cs — [ATO]-prefixed logging with verbosity control.
// ATOLog.cs — 带 [ATO] 前缀与级别控制的日志。
// -----------------------------------------------------------------------------

using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace net.fosa.ato.editor
{
    /// <summary>Central logger. All lines start with [ATO]. / 统一日志器，全部以 [ATO] 开头。</summary>
    internal static class ATOLog
    {
        /// <summary>Current verbosity; set from component at bake time. / 当前级别；烘焙时由组件设置。</summary>
        internal static net.fosa.ato.ATOLogLevel Level = net.fosa.ato.ATOLogLevel.Info;

        internal static bool IsEnabled(net.fosa.ato.ATOLogLevel level) => level <= Level;

        [Conditional("ATO_ENABLE_LOG")]
        internal static void Trace(string msg)
        {
            if (Level >= net.fosa.ato.ATOLogLevel.Trace) Debug.Log($"[ATO][TRACE] {msg}");
        }

        internal static void Debug(string msg)
        {
            if (Level >= net.fosa.ato.ATOLogLevel.Debug) UnityEngine.Debug.Log($"[ATO][DBG] {msg}");
        }

        internal static void Info(string msg)
        {
            if (Level >= net.fosa.ato.ATOLogLevel.Info) UnityEngine.Debug.Log($"[ATO] {msg}");
        }

        internal static void Warn(string msg)
        {
            if (Level >= net.fosa.ato.ATOLogLevel.Warning) UnityEngine.Debug.LogWarning($"[ATO][WARN] {msg}");
        }

        internal static void Error(string msg)
        {
            UnityEngine.Debug.LogError($"[ATO][ERROR] {msg}");
        }

        /// <summary>Log with an explicit level / 按指定级别输出。</summary>
        internal static void At(net.fosa.ato.ATOLogLevel level, string msg)
        {
            switch (level)
            {
                case net.fosa.ato.ATOLogLevel.Error: Error(msg); break;
                case net.fosa.ato.ATOLogLevel.Warning: Warn(msg); break;
                default: Info(msg); break;
            }
        }
    }
}
