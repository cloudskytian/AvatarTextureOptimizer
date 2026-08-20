using System;
using System.Threading;
using UnityEditor;

namespace Fosa.Ato.Editor
{
    /// <summary>
    /// Thread-safe progress + cancellation. Wraps EditorUtility.DisplayCancelableProgressBar on the
    /// main thread. Cancelling requests the pipeline to stop; temp assets on disk are kept while
    /// CPU/GPU/memory are released.
    /// 线程安全的进度与取消。在主线程封装 Unity 的可取消进度条；取消后终止流程，保留硬盘临时资产、释放资源。
    /// </summary>
    internal sealed class AtoProgress : IDisposable
    {
        private readonly string _title;
        private CancellationTokenSource _cts = new();
        private double _lastUpdate;
        private const double UpdateIntervalSec = 0.08;
        public CancellationToken Token => _cts.Token;
        public bool IsCancelled => _cts.IsCancellationRequested;

        public AtoProgress(string title = "Avatar Texture Optimizer")
        {
            _title = title;
        }

        public void Stage(string stage, float progress01, string detail = null)
        {
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastUpdate < UpdateIntervalSec && progress01 < 1f) return;
            _lastUpdate = now;
            bool cancelled = EditorUtility.DisplayCancelableProgressBar(
                _title,
                string.IsNullOrEmpty(detail) ? stage : $"{stage}\n{detail}",
                UnityEngine.Mathf.Clamp01(progress01));
            if (cancelled) Cancel();
        }

        public void Cancel()
        {
            if (!_cts.IsCancellationRequested)
            {
                AtoLog.Warn("Cancellation requested by user. / 用户请求取消。");
                _cts.Cancel();
            }
        }

        public void ThrowIfCancelled() => Token.ThrowIfCancellationRequested();

        public void Dispose()
        {
            EditorUtility.ClearProgressBar();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
