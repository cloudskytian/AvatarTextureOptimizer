// English: Central [ATO] logger with verbose switch and per-stage stopwatches.
// 中文：统一 [ATO] 日志，带详细开关与分阶段计时。
using System;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal sealed class ATOLogger
    {
        public bool Verbose;
        public readonly StringBuilder Detail = new StringBuilder(16 * 1024);
        public readonly StringBuilder Summary = new StringBuilder(2048);

        public ATOLogger(bool verbose)
        {
            Verbose = verbose;
        }

        public void Info(string message)
        {
            var line = AvatarTextureOptimizer.LogPrefix + " " + message;
            Debug.Log(line);
            Detail.AppendLine(line);
        }

        public void Warn(string message)
        {
            var line = AvatarTextureOptimizer.LogPrefix + " " + message;
            Debug.LogWarning(line);
            Detail.AppendLine(line);
        }

        public void Error(string message)
        {
            var line = AvatarTextureOptimizer.LogPrefix + " " + message;
            Debug.LogError(line);
            Detail.AppendLine(line);
        }

        public void VerboseInfo(string message)
        {
            if (!Verbose) return;
            Info(message);
        }

        public Scope Time(string stage)
        {
            return new Scope(this, stage);
        }

        public sealed class Scope : IDisposable
        {
            private readonly ATOLogger _log;
            private readonly string _stage;
            private readonly Stopwatch _sw;

            public Scope(ATOLogger log, string stage)
            {
                _log = log;
                _stage = stage;
                _sw = Stopwatch.StartNew();
                _log.VerboseInfo(">> " + stage);
            }

            public void Dispose()
            {
                _sw.Stop();
                var line = _stage + " finished in " + _sw.ElapsedMilliseconds + " ms";
                _log.Info(line);
            }
        }
    }
}
