using System;
using System.Threading;
using UnityEditor;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Cancelable progress. On cancel we throw ATOCanceledException; caller releases CPU/GPU/memory
    /// but leaves on-disk temp assets alone.
    /// 可取消进度。取消时抛 ATOCanceledException；调用方释放 CPU/GPU/内存，磁盘临时资产保留。
    /// </summary>
    public sealed class ATOProgress : IDisposable
    {
        private readonly string _title;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _disposed;

        public CancellationToken Token => _cts.Token;
        public bool IsCancellationRequested => _cts.IsCancellationRequested;

        public ATOProgress(string title)
        {
            _title = title;
        }

        public void Report(float normalized01, string stage)
        {
            if (_disposed) return;
            var canceled = EditorUtility.DisplayCancelableProgressBar(
                _title,
                stage,
                Math.Max(0f, Math.Min(1f, normalized01)));
            if (canceled)
            {
                _cts.Cancel();
                throw new ATOCanceledException(stage);
            }
            Token.ThrowIfCancellationRequested();
        }

        public void ThrowIfCanceled()
        {
            if (IsCancellationRequested) throw new ATOCanceledException("canceled");
            Token.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EditorUtility.ClearProgressBar();
            _cts.Dispose();
        }
    }

    public sealed class ATOCanceledException : OperationCanceledException
    {
        public string Stage { get; }

        public ATOCanceledException(string stage) : base("ATO canceled by user / 用户取消了 ATO")
        {
            Stage = stage;
        }
    }
}
