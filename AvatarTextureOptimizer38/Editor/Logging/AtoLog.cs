using System;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Central logger. All lines start with [ATO]. / 统一日志，均以 [ATO] 开头。
    /// </summary>
    public static class AtoLog
    {
        public static bool Verbose;

        public static void Info(string message)
        {
            Debug.Log($"{AvatarTextureOptimizer.LogPrefix} {message}");
        }

        public static void Warn(string message, Object context = null)
        {
            if (context != null) Debug.LogWarning($"{AvatarTextureOptimizer.LogPrefix} {message}", context);
            else Debug.LogWarning($"{AvatarTextureOptimizer.LogPrefix} {message}");
        }

        public static void Error(string message, Object context = null)
        {
            if (context != null) Debug.LogError($"{AvatarTextureOptimizer.LogPrefix} {message}", context);
            else Debug.LogError($"{AvatarTextureOptimizer.LogPrefix} {message}");
        }

        public static void VerboseLog(string message)
        {
            if (Verbose) Debug.Log($"{AvatarTextureOptimizer.LogPrefix} {message}");
        }

        public static Scope Time(string label)
        {
            return new Scope(label);
        }

        public struct Scope : IDisposable
        {
            private readonly string _label;
            private readonly Stopwatch _sw;

            public Scope(string label)
            {
                _label = label;
                _sw = Stopwatch.StartNew();
                VerboseLog($">> {_label}");
            }

            public void Dispose()
            {
                _sw.Stop();
                Info($"{_label} took {_sw.ElapsedMilliseconds} ms");
            }

            public long ElapsedMs => _sw.ElapsedMilliseconds;
        }
    }

    /// <summary>
    /// Thrown when the user clicks Cancel on the progress bar. / 用户在进度条点取消时抛出。
    /// </summary>
    public sealed class AtoCanceledException : Exception
    {
        public AtoCanceledException() : base("ATO bake canceled by user / 用户取消了烘焙") { }
    }
}
