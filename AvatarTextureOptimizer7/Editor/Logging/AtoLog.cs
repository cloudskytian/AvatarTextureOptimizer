using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// All ATO logs start with [ATO]. Verbose lines are gated by the component toggle.
    /// 所有 ATO 日志以 [ATO] 开头。详细日志受组件开关控制。
    /// </summary>
    public sealed class AtoLog
    {
        public const string Prefix = "[ATO]";

        public bool Verbose;
        readonly StringBuilder _detail = new StringBuilder(16 * 1024);
        readonly Stopwatch _total = Stopwatch.StartNew();

        public TimeSpan Elapsed => _total.Elapsed;

        public void Info(string message)
        {
            var line = Prefix + " " + message;
            Debug.Log(line);
            _detail.AppendLine(line);
        }

        public void Warn(string message)
        {
            var line = Prefix + " WARN " + message;
            Debug.LogWarning(line);
            _detail.AppendLine(line);
        }

        public void Error(string message)
        {
            var line = Prefix + " ERROR " + message;
            Debug.LogError(line);
            _detail.AppendLine(line);
        }

        public void VerboseInfo(string message)
        {
            var line = Prefix + " " + message;
            _detail.AppendLine(line);
            if (Verbose) Debug.Log(line);
        }

        public StageScope Stage(string name)
        {
            return new StageScope(this, name);
        }

        public string GetDetailDump() => _detail.ToString();

        public readonly struct StageScope : IDisposable
        {
            readonly AtoLog _log;
            readonly string _name;
            readonly Stopwatch _sw;

            public StageScope(AtoLog log, string name)
            {
                _log = log;
                _name = name;
                _sw = Stopwatch.StartNew();
                log.Info(">> " + name);
            }

            public void Dispose()
            {
                _sw.Stop();
                _log.Info(string.Format(CultureInfo.InvariantCulture,
                    "<< {0}  ({1:0.0} ms)", _name, _sw.Elapsed.TotalMilliseconds));
            }
        }
    }
}
