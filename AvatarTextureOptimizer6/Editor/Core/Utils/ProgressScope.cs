using System;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using UnityEditor;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.Utils
{
    /// <summary>
    /// 进度与取消。烘焙/构建期间显示进度条，支持用户取消；取消后释放资源。
    /// Progress bar + user cancellation.
    /// </summary>
    public sealed class ProgressScope : IDisposable
    {
        private readonly string _title;
        private readonly ATOLogger _logger;

        /// <summary>取消标记（由 EditorApplication.update 轮询）。</summary>
        public volatile bool Cancelled;

        public ProgressScope(string title, ATOLogger logger)
        {
            _title = title;
            _logger = logger;
        }

        public void Report(float progress01, string info)
        {
            if (Cancelled) return;
            if (EditorUtility.DisplayCancelableProgressBar(_title, info, Mathf.Clamp01(progress01)))
            {
                Cancelled = true;
                _logger.Warn("Cancellation requested by user.");
            }
        }

        /// <summary>检查是否取消；取消时抛出 ATOBuildCancelledException 中止流程。</summary>
        public void ThrowIfCancelled()
        {
            if (Cancelled)
            {
                EditorUtility.ClearProgressBar();
                throw new ATOBuildCancelledException("Build cancelled by user.");
            }
        }

        public void Dispose()
        {
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>用户取消烘焙时抛出的异常（调用方捕获后清理资源、保留磁盘临时资产）。</summary>
    public sealed class ATOBuildCancelledException : Exception
    {
        public ATOBuildCancelledException(string message) : base(message) { }
    }
}
