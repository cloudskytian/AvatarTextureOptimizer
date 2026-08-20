using System;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Central logger. Every line starts with [ATO]. / 统一日志，每行以 [ATO] 开头。
    /// </summary>
    public static class AtoLog
    {
        public const string Prefix = "[ATO]";
        public static bool Verbose;

        public static void Info(string message)
        {
            Debug.Log($"{Prefix} {message}");
        }

        public static void Warn(string message)
        {
            Debug.LogWarning($"{Prefix} {message}");
        }

        public static void Error(string message)
        {
            Debug.LogError($"{Prefix} {message}");
        }

        public static void VerboseInfo(string message)
        {
            if (Verbose) Debug.Log($"{Prefix} {message}");
        }

        public static Stopwatch Start(string step)
        {
            VerboseInfo($"BEGIN {step}");
            var sw = Stopwatch.StartNew();
            return sw;
        }

        public static void End(string step, Stopwatch sw, string extra = null)
        {
            sw.Stop();
            var msg = $"END {step}  {sw.Elapsed.TotalMilliseconds:F1} ms";
            if (!string.IsNullOrEmpty(extra)) msg += "  " + extra;
            Info(msg);
        }
    }

    /// <summary>
    /// Thrown when the user cancels bake. / 用户取消烘焙时抛出。
    /// </summary>
    public sealed class AtoCanceledException : Exception
    {
        public AtoCanceledException() : base("ATO bake canceled by user / 用户取消了 ATO 烘焙") { }
    }
}
