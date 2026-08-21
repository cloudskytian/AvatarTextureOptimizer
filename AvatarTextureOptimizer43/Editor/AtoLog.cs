using System;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// Central logger. Every line starts with [ATO]. Verbose lines are gated.
    /// 统一日志。每行以 [ATO] 开头。细节日志受开关控制。
    /// </summary>
    public static class AtoLog
    {
        public static bool Verbose;

        public static void Info(string msg)
        {
            Debug.Log(AvatarTextureOptimizer.LogPrefix + " " + msg);
        }

        public static void Warn(string msg)
        {
            Debug.LogWarning(AvatarTextureOptimizer.LogPrefix + " " + msg);
        }

        public static void Error(string msg)
        {
            Debug.LogError(AvatarTextureOptimizer.LogPrefix + " " + msg);
        }

        public static void Detail(string msg)
        {
            if (Verbose) Debug.Log(AvatarTextureOptimizer.LogPrefix + " " + msg);
        }

        /// <summary>Stage timer. Dispose to log elapsed ms. 阶段计时器。</summary>
        public static Scope Time(string stage)
        {
            return new Scope(stage);
        }

        public struct Scope : IDisposable
        {
            readonly string _stage;
            readonly Stopwatch _sw;
            public Scope(string stage)
            {
                _stage = stage;
                _sw = Stopwatch.StartNew();
                Detail("BEGIN " + stage);
            }
            public long ElapsedMs => _sw.ElapsedMilliseconds;
            public void Dispose()
            {
                _sw.Stop();
                Info(_stage + "  " + _sw.ElapsedMilliseconds + " ms");
            }
        }

        public static string Bytes(long n)
        {
            if (n < 1024) return n + " B";
            if (n < 1024 * 1024) return (n / 1024.0).ToString("0.0") + " KB";
            return (n / (1024.0 * 1024.0)).ToString("0.00") + " MB";
        }
    }
}
