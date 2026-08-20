using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.UIElements;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// A simple NDMF console entry with pre-localized literal text. / 一个使用预本地化字面文本的简单 NDMF 控制台条目。
    /// Used for ATO warnings/errors/report (messages are already resolved to the avatar's language). /
    /// 用于 ATO 的警告/错误/报告（消息已按 Avatar 语言解析）。
    /// </summary>
    internal sealed class AtoConsoleEntry : IError
    {
        private readonly string _title;
        private readonly string _details;
        private readonly ErrorSeverity _severity;
        private readonly List<ObjectReference> _references = new List<ObjectReference>();

        public AtoConsoleEntry(string title, ErrorSeverity severity) : this(title, null, severity) { }

        public AtoConsoleEntry(string title, string details, ErrorSeverity severity)
        {
            _title = title;
            _details = details;
            _severity = severity;
        }

        public ErrorSeverity Severity => _severity;

        public void AddReference(ObjectReference obj)
        {
            if (obj != null && !_references.Contains(obj)) _references.Add(obj);
        }

        public VisualElement CreateVisualElement(ErrorReport report)
        {
            var container = new VisualElement();
            var title = new Label(_title)
            {
                style = { whiteSpace = WhiteSpace.Normal },
            };
            container.Add(title);

            if (!string.IsNullOrEmpty(_details))
            {
                // Details are folded by default (summary always visible). / 细节默认折叠（摘要常显）。
                var foldout = new Foldout { text = "ATO details", value = false };
                var detailsLabel = new Label(_details)
                {
                    style = { whiteSpace = WhiteSpace.Normal },
                };
                foldout.Add(detailsLabel);
                container.Add(foldout);
            }
            return container;
        }

        public string ToMessage() => string.IsNullOrEmpty(_details) ? _title : _title + "\n" + _details;
    }
}
