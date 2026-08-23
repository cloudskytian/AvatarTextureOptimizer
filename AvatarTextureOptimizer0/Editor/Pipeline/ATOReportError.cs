using nadena.dev.ndmf;
using UnityEngine.UIElements;

namespace Fosa.AvatarTextureOptimizer.Editor.Pipeline
{
    internal sealed class ATOReportError : IError
    {
        private readonly string _message;
        public ATOReportError(ErrorSeverity severity, string message) { Severity = severity; _message = message; }
        public ErrorSeverity Severity { get; }
        public VisualElement CreateVisualElement(ErrorReport report)
        {
            return new Label(_message) { style = { whiteSpace = WhiteSpace.Normal } };
        }
        public string ToMessage() => "[ATO] " + _message;
        public void AddReference(ObjectReference obj) { }
    }
}
