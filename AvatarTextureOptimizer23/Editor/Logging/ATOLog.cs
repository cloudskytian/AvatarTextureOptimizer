using System;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Central logger. Every line starts with [ATO]. A switch is reserved for advanced users.
    /// 中央日志。每行以 [ATO] 开头。预留开关给高级用户。
    /// </summary>
    public sealed class ATOLog
    {
        public bool Enabled { get; set; } = true;

        private readonly StringBuilder _report = new StringBuilder(8 * 1024);
        private readonly StringBuilder _detail = new StringBuilder(32 * 1024);

        public string SummaryText => _report.ToString();
        public string DetailText => _detail.ToString();

        public void Info(string message)
        {
            Write("INFO", message, false);
        }

        public void Warn(string message)
        {
            Write("WARN", message, true);
        }

        public void Error(string message)
        {
            var line = $"{AvatarTextureOptimizer.LogPrefix} ERROR {message}";
            _report.AppendLine(line);
            _detail.AppendLine(line);
            Debug.LogError(line);
        }

        public void Detail(string message)
        {
            var line = $"{AvatarTextureOptimizer.LogPrefix} {message}";
            _detail.AppendLine(line);
            if (Enabled) Debug.Log(line);
        }

        public IDisposable Stage(string name)
        {
            return new StageScope(this, name);
        }

        private void Write(string level, string message, bool warning)
        {
            var line = $"{AvatarTextureOptimizer.LogPrefix} {level} {message}";
            _report.AppendLine(line);
            _detail.AppendLine(line);
            if (!Enabled && !warning) return;
            if (warning) Debug.LogWarning(line);
            else Debug.Log(line);
        }

        private sealed class StageScope : IDisposable
        {
            private readonly ATOLog _log;
            private readonly string _name;
            private readonly Stopwatch _sw;

            public StageScope(ATOLog log, string name)
            {
                _log = log;
                _name = name;
                _sw = Stopwatch.StartNew();
                _log.Info($">> {_name}");
            }

            public void Dispose()
            {
                _sw.Stop();
                _log.Info($"<< {_name}  {_sw.Elapsed.TotalMilliseconds:F1} ms");
            }
        }
    }
}
