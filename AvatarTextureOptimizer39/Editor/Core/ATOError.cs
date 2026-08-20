// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvatarTextureOptimizer.Editor.Core
{
    /// <summary>
    /// Minimal NDMF error type carrying a fixed bilingual message. Avoids the full
    /// localization-asset plumbing for developer-facing errors while still surfacing in
    /// the NDMF console.
    ///
    /// 携带固定双语消息的最小 NDMF 错误类型。避免为面向开发者/用户的错误接入完整本地化
    /// 资产管线，同时仍能在 NDMF 控制台展示。
    /// </summary>
    public sealed class ATOMessageError : SimpleError
    {
        private readonly string _message;
        private readonly ErrorSeverity _severity;

        public ATOMessageError(string message, ErrorSeverity severity)
        {
            _message = message;
            _severity = severity;
        }

        public override Localizer Localizer => null;
        public override string TitleKey => _message;
        public override string DetailsKey => null;
        public override string HintKey => null;
        public override ErrorSeverity Severity => _severity;

        public override string ToMessage() => "[ATO] " + _message;

        // Override formatting to avoid touching the null Localizer. 覆写格式化以避开 null Localizer。
        public override string FormatTitle() => "[ATO] " + _message;
        public override string FormatDetails() => null;
        public override string FormatHint() => null;

        public override VisualElement CreateVisualElement(ErrorReport report)
        {
            var ve = new VisualElement();
            ve.Add(new Label(ToMessage()));
            return ve;
        }
    }

    /// <summary>
    /// Convenience static helpers to report ATO errors into the active NDMF report.
    /// 便捷静态方法：把 ATO 错误写入当前 NDMF 报告。
    /// </summary>
    public static class ATOError
    {
        public static void Report(string message, ErrorSeverity severity = ErrorSeverity.Error,
            params Object[] references)
        {
            var err = new ATOMessageError(message, severity);
            foreach (var r in references)
            {
                if (r == null) continue;
                try { err.AddReference(ObjectRegistry.GetReference(r)); }
                catch (System.Exception) { /* reference add is best-effort. 引用添加尽力而为。 */ }
            }
            ErrorReport.ReportError(err);
        }
    }
}
