using System;
using UnityEditor;

namespace Fosa.AvatarTextureOptimizer.Editor.Core
{
    /// <summary>EN: Cooperative build cancellation through Unity's progress UI. ZH: 通过 Unity 进度 UI 协作取消构建。</summary>
    internal sealed class BuildProgress : IDisposable
    {
        private readonly string _title;
        private bool _disposed;
        public BuildProgress(string title) { _title = title; }

        public void Report(string stage, int completed, int total)
        {
            var progress = total <= 0 ? 0f : Math.Max(0f, Math.Min(1f, (float)completed / total));
            if (EditorUtility.DisplayCancelableProgressBar(_title, "[ATO] " + stage, progress))
                throw new OperationCanceledException("Avatar Texture Optimizer build cancelled by user.");
        }

        public void ThrowIfCancelled(string stage) => Report(stage, 0, 1);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EditorUtility.ClearProgressBar();
        }
    }
}
