using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: One timed step in the build. Steps nest, so the report can show a tree.
    /// ZH: 构建中的一个计时步骤。步骤可嵌套，因此报告可以呈现为树。
    /// </summary>
    public sealed class ATOTimingNode
    {
        /// <summary>EN: Human readable step name. ZH: 人类可读的步骤名。</summary>
        public string Name;
        /// <summary>EN: Wall clock milliseconds spent inside this step. ZH: 该步骤耗费的墙钟毫秒数。</summary>
        public double Milliseconds;
        /// <summary>EN: Nested steps. ZH: 嵌套的子步骤。</summary>
        public readonly List<ATOTimingNode> Children = new List<ATOTimingNode>();
        /// <summary>EN: Optional extra detail lines. ZH: 可选的补充明细行。</summary>
        public readonly List<string> Details = new List<string>();
    }

    /// <summary>
    /// EN: Central logging facility. Every message is prefixed with [ATO]. Verbose and trace levels are
    ///     gated behind user toggles so a shipped build stays quiet, but the call sites are permanent -
    ///     we deliberately instrument every step up front instead of adding logs after a bug appears.
    /// ZH: 集中式日志设施。所有消息均以 [ATO] 开头。Verbose 与 Trace 级别由用户开关控制，
    ///     以便正式使用时保持安静；但调用点是永久保留的——我们刻意在一开始就给每一步埋点，
    ///     而不是等出了 bug 再临时加日志。
    /// </summary>
    public sealed class ATOLog
    {
        private readonly bool _verbose;
        private readonly bool _trace;
        private readonly Stack<ATOTimingNode> _stack = new Stack<ATOTimingNode>();

        /// <summary>EN: Root of the timing tree. ZH: 计时树的根节点。</summary>
        public readonly ATOTimingNode Root = new ATOTimingNode { Name = "Avatar Texture Optimizer" };

        /// <summary>EN: Warnings accumulated during the build, surfaced in the NDMF console.
        /// ZH: 构建期间累积的警告，会呈现到 NDMF 控制台。</summary>
        public readonly List<string> Warnings = new List<string>();

        /// <summary>EN: Create a logger. ZH: 创建日志器。</summary>
        /// <param name="verbose">EN: enable per-step logs. ZH: 启用分步日志。</param>
        /// <param name="trace">EN: enable per-island logs. ZH: 启用逐岛日志。</param>
        public ATOLog(bool verbose, bool trace)
        {
            _verbose = verbose || trace;
            _trace = trace;
            _stack.Push(Root);
        }

        /// <summary>EN: True when verbose logging is on. ZH: 是否开启了详细日志。</summary>
        public bool VerboseEnabled => _verbose;

        /// <summary>EN: True when trace logging is on. ZH: 是否开启了逐项跟踪日志。</summary>
        public bool TraceEnabled => _trace;

        /// <summary>EN: Always-on informational message. ZH: 始终输出的信息级消息。</summary>
        public void Info(string msg) => Debug.Log($"{ATOConstants.LogPrefix} {msg}");

        /// <summary>EN: Verbose message, suppressed unless the user enabled verbose logging.
        /// ZH: 详细消息，未开启详细日志时不输出。</summary>
        public void Verbose(string msg)
        {
            if (_verbose) Debug.Log($"{ATOConstants.LogPrefix} {msg}");
        }

        /// <summary>EN: Trace message, suppressed unless the user enabled trace logging.
        /// ZH: 跟踪消息，未开启跟踪日志时不输出。</summary>
        public void Trace(string msg)
        {
            if (_trace) Debug.Log($"{ATOConstants.LogPrefix} {msg}");
        }

        /// <summary>EN: Record a warning both in the Unity console and in the build report.
        /// ZH: 同时把警告记录到 Unity 控制台与构建报告。</summary>
        public void Warn(string msg)
        {
            Warnings.Add(msg);
            Debug.LogWarning($"{ATOConstants.LogPrefix} {msg}");
        }

        /// <summary>EN: Record an error. ZH: 记录一条错误。</summary>
        public void Error(string msg) => Debug.LogError($"{ATOConstants.LogPrefix} {msg}");

        /// <summary>EN: Attach a detail line to the currently open timing step. ZH: 给当前打开的计时步骤附加一行明细。</summary>
        public void Detail(string msg)
        {
            _stack.Peek().Details.Add(msg);
            Trace($"  {msg}");
        }

        /// <summary>
        /// EN: Open a timed step. Dispose the returned handle (or use a using-block) to close it.
        /// ZH: 打开一个计时步骤。释放返回的句柄（或使用 using 块）以关闭它。
        /// </summary>
        public IDisposable Step(string name)
        {
            var node = new ATOTimingNode { Name = name };
            _stack.Peek().Children.Add(node);
            _stack.Push(node);
            Verbose($"> {name}");
            return new StepHandle(this, node);
        }

        private sealed class StepHandle : IDisposable
        {
            private readonly ATOLog _log;
            private readonly ATOTimingNode _node;
            private readonly Stopwatch _sw = Stopwatch.StartNew();

            public StepHandle(ATOLog log, ATOTimingNode node)
            {
                _log = log;
                _node = node;
            }

            public void Dispose()
            {
                _sw.Stop();
                _node.Milliseconds = _sw.Elapsed.TotalMilliseconds;
                _log._stack.Pop();
                _log.Verbose($"< {_node.Name} ({_node.Milliseconds:F1} ms)");
            }
        }

        /// <summary>
        /// EN: Render the timing tree as an indented plain-text block for the NDMF report.
        /// ZH: 把计时树渲染成缩进的纯文本块，供 NDMF 报告使用。
        /// </summary>
        public string FormatTimings()
        {
            var sb = new StringBuilder();
            void Walk(ATOTimingNode n, int depth)
            {
                sb.Append(' ', depth * 2)
                  .Append(n.Name)
                  .Append("  ")
                  .Append(n.Milliseconds.ToString("F1"))
                  .AppendLine(" ms");
                foreach (var d in n.Details) sb.Append(' ', depth * 2 + 2).AppendLine(d);
                foreach (var c in n.Children) Walk(c, depth + 1);
            }
            foreach (var c in Root.Children) Walk(c, 0);
            return sb.ToString();
        }
    }
}
