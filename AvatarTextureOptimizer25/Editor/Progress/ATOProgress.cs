// Avatar Texture Optimizer / 头像贴图优化器
// Progress display + cooperative cancellation around the whole pipeline.
// 整条管线的进度显示与协作式取消。
//
// Uses UnityEditor.Progress so the user gets a cancellable progress bar in the
// editor status area / VRC SDK build window. Cancelling aborts the bake/build,
// keeps on-disk temporary assets (per requirement) but releases all CPU/GPU
// and native memory resources through the pipeline's resource scope.
// 基于 UnityEditor.Progress，向用户提供可取消的进度条。取消时中止烘焙/构建、
// 保留磁盘上的临时资产（需求要求），同时通过管线的资源作用域释放全部
// CPU/GPU/Native 资源。

using System;
using System.Threading;
using UnityEditor;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Thrown when the user cancels the build. / 用户取消构建时抛出。</summary>
    public sealed class ATOCancelledException : OperationCanceledException
    {
        public ATOCancelledException() : base("ATO pipeline cancelled by user / 用户取消了 ATO 管线") { }
    }

    /// <summary>
    /// Stage/scoped progress reporter with cancellation support.
    /// 分阶段、可作用域嵌套的进度上报器（支持取消）。
    /// </summary>
    public sealed class ATOProgress : IDisposable
    {
        private readonly int _rootId;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _disposed;

        /// <summary>Cancellation token observed by every stage. / 各阶段观测的取消令牌。</summary>
        public CancellationToken Token => _cts.Token;

        /// <summary>True after the user pressed cancel. / 用户按下取消后为真。</summary>
        public bool IsCancellationRequested => _cts.IsCancellationRequested;

        public ATOProgress(string title, string description = "")
        {
            _rootId = Progress.Start(title, description, Progress.Options.Sticky | Progress.Options.Managed);
            Progress.RegisterCancelCallback(_rootId, () =>
            {
                _cts.Cancel();
                return true;
            });
        }

        /// <summary>
        /// Reports progress and throws <see cref="ATOCancelledException"/> if cancelled.
        /// 上报进度；若用户已取消则抛出 <see cref="ATOCancelledException"/>。
        /// </summary>
        public void Report(string step, float progress, string detail = null)
        {
            if (_cts.IsCancellationRequested)
            {
                Cleanup();
                throw new ATOCancelledException();
            }
            Progress.Report(_rootId, progress, detail);
            Progress.SetDescription(_rootId, step);
        }

        /// <summary>Cancellation checkpoint only. / 仅作取消检查点。</summary>
        public void ThrowIfCancelled()
        {
            if (_cts.IsCancellationRequested)
            {
                Cleanup();
                throw new ATOCancelledException();
            }
        }

        private void Cleanup()
        {
            if (_disposed) return;
            _disposed = true;
            try { Progress.UnregisterCancelCallback(_rootId); } catch { /* best effort / 尽力而为 */ }
            try { Progress.Remove(_rootId); } catch { /* best effort */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Progress.Finish(_rootId); } catch { /* best effort */ }
            _cts.Dispose();
        }
    }
}
