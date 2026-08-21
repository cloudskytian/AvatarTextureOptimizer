using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace net.fosa.ato
{
    /// <summary>
    /// 报告到 NDMF 控制台的错误/警告实现 / Custom IError implementation reported to the NDMF console.
    /// 标题/详情/提示均走 ATO i18n / Title, details and hint are localized through ATOI18n.
    /// </summary>
    internal sealed class ATOError : IError
    {
        public ErrorSeverity Severity { get; }
        private readonly string _titleKey;
        private readonly string _detailsKey;
        private readonly string _hintKey;
        private readonly object[] _subst;
        private readonly System.Collections.Generic.List<ObjectReference> _refs = new System.Collections.Generic.List<ObjectReference>();

        public ATOError(ErrorSeverity severity, string titleKey, string detailsKey = null, string hintKey = null, params object[] subst)
        {
            Severity = severity;
            _titleKey = titleKey;
            _detailsKey = detailsKey;
            _hintKey = hintKey;
            _subst = subst;
        }

        public ATOError AddRef(Object obj)
        {
            if (obj != null)
            {
                try { _refs.Add(ObjectRegistry.GetReference(obj)); }
                catch { /* 注册失败时忽略 / ignore registration failures */ }
            }

            return this;
        }

        public VisualElement CreateVisualElement(ErrorReport report)
        {
            // 简单文本展示 / simple text display
            var root = new VisualElement();
            root.style.paddingTop = 4;
            root.style.paddingBottom = 4;
            var title = new Label(ToMessage());
            root.Add(title);
            if (_refs.Count > 0)
            {
                var refs = new Label(string.Join("\n", _refs.ConvertAll(r => r.ToString())));
                refs.style.unityFontStyleAndWeight = FontStyle.Italic;
                root.Add(refs);
            }

            return root;
        }

        public string ToMessage()
        {
            string msg = ATOI18n.T(_titleKey, _subst ?? System.Array.Empty<object>());
            if (!string.IsNullOrEmpty(_detailsKey))
            {
                string d = ATOI18n.T(_detailsKey, _subst ?? System.Array.Empty<object>());
                if (!string.IsNullOrEmpty(d) && d != _detailsKey) msg += "\n" + d;
            }

            if (!string.IsNullOrEmpty(_hintKey))
            {
                string h = ATOI18n.T(_hintKey, _subst ?? System.Array.Empty<object>());
                if (!string.IsNullOrEmpty(h) && h != _hintKey) msg += "\n" + h;
            }

            return msg;
        }

        public void AddReference(ObjectReference obj)
        {
            _refs.Add(obj);
        }
    }

    /// <summary>
    /// 报告辅助 / Reporting helpers.
    /// </summary>
    internal static class ATOReport
    {
        public static void Info(string titleKey, string detailsKey = null, string hintKey = null, params object[] subst)
        {
            ErrorReport.ReportError(new ATOError(ErrorSeverity.Information, titleKey, detailsKey, hintKey, subst));
        }

        public static void Warn(string titleKey, string detailsKey = null, string hintKey = null, params object[] subst)
        {
            ErrorReport.ReportError(new ATOError(ErrorSeverity.NonFatal, titleKey, detailsKey, hintKey, subst));
        }

        public static void Error(string titleKey, string detailsKey = null, string hintKey = null, params object[] subst)
        {
            ErrorReport.ReportError(new ATOError(ErrorSeverity.Error, titleKey, detailsKey, hintKey, subst));
        }
    }
}
