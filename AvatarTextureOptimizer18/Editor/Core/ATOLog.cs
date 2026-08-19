using System;
using System.Diagnostics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    // [ATO] 前缀日志器 + 计时作用域。Prefixed logger and timing scopes.
    // 所有日志以 [ATO] 开头；Verbose 开关供高级用户控制详细日志（默认开启，开发阶段每步主动输出）。
    internal static class ATOLog
    {
        public static bool Verbose = true;

        public static void Info(string msg)
        {
            UnityEngine.Debug.Log(ATOConstants.LogPrefix + " " + msg);
        }

        public static void Warn(string msg)
        {
            UnityEngine.Debug.LogWarning(ATOConstants.LogPrefix + " " + msg);
        }

        public static void Error(string msg)
        {
            UnityEngine.Debug.LogError(ATOConstants.LogPrefix + " " + msg);
        }

        // 仅详细模式输出。Logs only in verbose mode.
        public static void Debug(string msg)
        {
            if (Verbose) UnityEngine.Debug.Log(ATOConstants.LogPrefix + " [DBG] " + msg);
        }

        // 计时作用域：Dispose 时输出耗时。Timing scope: logs elapsed time on dispose.
        public static ATOTimer Time(string stageName)
        {
            return new ATOTimer(stageName);
        }
    }

    // 计时器。Timer.
    internal sealed class ATOTimer : IDisposable
    {
        private readonly string _name;
        private readonly Stopwatch _sw;

        public ATOTimer(string name)
        {
            _name = name;
            _sw = Stopwatch.StartNew();
        }

        public double ElapsedMs => _sw.Elapsed.TotalMilliseconds;

        public void Dispose()
        {
            _sw.Stop();
            ATOLog.Info(string.Format("阶段 / Stage '{0}' 耗时 / took {1:F1} ms", _name, _sw.Elapsed.TotalMilliseconds));
        }
    }
}
