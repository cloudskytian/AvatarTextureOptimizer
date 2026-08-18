using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Prefixed logger. / 带 [ATO] 前缀的日志。
    /// </summary>
    public static class AtoLog
    {
        public const string Prefix = "[ATO]";
        public static bool Verbose;

        public static void Info(string msg) => Debug.Log($"{Prefix} {msg}");
        public static void Warn(string msg) => Debug.LogWarning($"{Prefix} {msg}");
        public static void Error(string msg) => Debug.LogError($"{Prefix} {msg}");

        public static void VerboseInfo(string msg)
        {
            if (Verbose) Debug.Log($"{Prefix} {msg}");
        }

        public static Stopwatch Start(string label)
        {
            VerboseInfo($"begin: {label}");
            return Stopwatch.StartNew();
        }

        public static void End(Stopwatch sw, string label)
        {
            sw.Stop();
            Info($"{label} took {sw.ElapsedMilliseconds} ms");
        }
    }
}
