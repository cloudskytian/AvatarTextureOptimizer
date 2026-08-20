using System;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Central logger. Prefix [ATO]. / 统一日志，前缀 [ATO]。
    /// </summary>
    public static class AtoLog
    {
        public static bool Verbose = true;

        public static void Info(string message)
        {
            Debug.Log("[ATO] " + message);
        }

        public static void Warn(string message)
        {
            Debug.LogWarning("[ATO] " + message);
        }

        public static void Error(string message)
        {
            Debug.LogError("[ATO] " + message);
        }

        public static void VerboseInfo(string message)
        {
            if (Verbose) Info(message);
        }

        public static Scope Time(string step)
        {
            return new Scope(step);
        }

        public readonly struct Scope : IDisposable
        {
            private readonly string _step;
            private readonly Stopwatch _sw;

            public Scope(string step)
            {
                _step = step;
                _sw = Stopwatch.StartNew();
                if (Verbose) Debug.Log("[ATO] BEGIN " + step);
            }

            public void Dispose()
            {
                _sw.Stop();
                Debug.Log("[ATO] END " + _step + " elapsed=" + _sw.ElapsedMilliseconds + "ms");
            }
        }
    }
}
