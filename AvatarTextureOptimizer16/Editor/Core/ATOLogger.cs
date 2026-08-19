using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// A plain-text NDMF error/warning with [ATO] prefix, using our own i18n.
    /// [ATO] 前缀的纯文本 NDMF 错误/警告，使用我们自己的 i18n。
    /// </summary>
    public sealed class ATOSimpleError : nadena.dev.ndmf.IError
    {
        public nadena.dev.ndmf.ErrorSeverity Severity { get; }
        private readonly string _message;
        private readonly UnityEngine.Object _context;

        public ATOSimpleError(nadena.dev.ndmf.ErrorSeverity severity, string message, UnityEngine.Object context = null)
        {
            Severity = severity;
            _message = message;
            _context = context;
        }

        public UnityEngine.UIElements.VisualElement CreateVisualElement(nadena.dev.ndmf.ErrorReport report)
        {
            var label = new UnityEngine.UIElements.Label(_message);
            label.style.whiteSpace = UnityEngine.UIElements.WhiteSpace.Normal;
            return label;
        }

        public string ToMessage() => "[ATO] " + _message;

        public void AddReference(nadena.dev.ndmf.ObjectReference obj)
        {
        }
    }

    /// <summary>
    /// Logging facade. All output is prefixed with [ATO]. / 日志门面。所有输出以 [ATO] 开头。
    /// Verbose detail logging can be toggled via <see cref="Verbose"/> for advanced debugging.
    /// 详细日志可通过 <see cref="Verbose"/> 开关，供高级用户调试。
    /// </summary>
    public static class ATOLogger
    {
        /// <summary>Enable verbose per-step logs. / 开启每步详细日志。</summary>
        public static bool Verbose = false;

        public static void Info(string msg)
        {
            Debug.Log("[ATO] " + msg);
        }

        public static void Warn(string msg, UnityEngine.Object ctx = null)
        {
            Debug.LogWarning("[ATO] " + msg, ctx);
            nadena.dev.ndmf.ErrorReport.ReportError(
                new ATOSimpleError(nadena.dev.ndmf.ErrorSeverity.NonFatal, msg, ctx));
        }

        public static void Error(string msg, UnityEngine.Object ctx = null)
        {
            Debug.LogError("[ATO] " + msg, ctx);
            nadena.dev.ndmf.ErrorReport.ReportError(
                new ATOSimpleError(nadena.dev.ndmf.ErrorSeverity.Error, msg, ctx));
        }

        public static void InfoDetail(string msg)
        {
            if (Verbose) Debug.Log("[ATO]   " + msg);
        }

        /// <summary>
        /// Report a warning that a texture was treated as whitelisted (skipped) and why.
        /// 报告贴图因故被视作白名单（跳过）及原因。
        /// </summary>
        public static void SkipWarning(string reason, UnityEngine.Object ctx = null)
        {
            Warn("skipped (treated as whitelist): " + reason, ctx);
        }

        /// <summary>
        /// Measure and log the elapsed time of an operation. / 计时并记录某操作的耗时。
        /// </summary>
        public static IDisposable Timed(string what)
        {
            return new Timing(what);
        }

        private sealed class Timing : IDisposable
        {
            private readonly string _what;
            private readonly Stopwatch _sw;

            public Timing(string what)
            {
                _what = what;
                _sw = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                _sw.Stop();
                Info($"{_what}: {_sw.ElapsedMilliseconds} ms");
            }
        }
    }
}
