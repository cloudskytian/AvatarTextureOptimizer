using System;
using UnityEditor;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// Cancelable progress bar. Throws OperationCanceledException on cancel.
    /// 可取消进度条。取消时抛出 OperationCanceledException。
    /// </summary>
    public sealed class AtoProgress : IDisposable
    {
        bool _disposed;
        public string Stage { get; private set; } = "";
        public float Value { get; private set; }

        public void Set(string stage, float t01)
        {
            Stage = stage ?? "";
            Value = t01;
            if (EditorUtility.DisplayCancelableProgressBar(
                    AvatarTextureOptimizer.DisplayName,
                    Stage,
                    Math.Clamp(t01, 0f, 1f)))
            {
                throw new OperationCanceledException("ATO cancelled by user");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EditorUtility.ClearProgressBar();
        }
    }
}
