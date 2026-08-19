using System.Collections.Generic;
using System.Text;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.UIElements;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Foldable NDMF console report. Summary is always visible; details start collapsed.
    /// 可折叠的 NDMF 控制台报告。默认只显示总览，细节折叠。
    /// </summary>
    public sealed class AtoReportError : IError
    {
        readonly string _title;
        readonly string _summary;
        readonly string _details;
        readonly List<ObjectReference> _refs = new List<ObjectReference>();

        public AtoReportError(string title, string summary, string details)
        {
            _title = title;
            _summary = summary;
            _details = details ?? "";
        }

        public ErrorSeverity Severity => ErrorSeverity.Information;

        public void AddReference(ObjectReference obj)
        {
            if (obj != null) _refs.Add(obj);
        }

        public string ToMessage()
        {
            return _title + "\n" + _summary + "\n" + _details;
        }

        public VisualElement CreateVisualElement(ErrorReport report)
        {
            var root = new VisualElement();
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 6;
            root.style.paddingBottom = 6;

            var title = new Label(_title);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 13;
            root.Add(title);

            var summary = new Label(_summary);
            summary.style.whiteSpace = WhiteSpace.Normal;
            summary.style.marginTop = 4;
            root.Add(summary);

            if (!string.IsNullOrEmpty(_details))
            {
                var fold = new Foldout { text = "Details / 详细信息", value = false };
                fold.style.marginTop = 6;
                var body = new Label(_details);
                body.style.whiteSpace = WhiteSpace.Normal;
                body.style.unityFont = Font.CreateDynamicFontFromOSFont("Consolas", 11);
                fold.Add(body);
                root.Add(fold);
            }

            return root;
        }
    }
}
