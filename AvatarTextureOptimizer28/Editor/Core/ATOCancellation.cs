using System;
using System.Threading;
using UnityEditor;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: Thrown when the user cancels the bake. Caught by the pass, which then releases every
    ///     GPU/CPU resource but intentionally leaves temporary assets on disk so a partially written
    ///     asset container is not corrupted.
    /// ZH: 用户取消烘焙时抛出。由 Pass 捕获，随后释放全部 GPU/CPU 资源，
    ///     但刻意保留硬盘上的临时资产，以免部分写入的资产容器被破坏。
    /// </summary>
    public sealed class ATOCancelledException : OperationCanceledException
    {
        /// <summary>EN: Construct with the default message. ZH: 使用默认消息构造。</summary>
        public ATOCancelledException() : base("[ATO] Bake cancelled by user.") { }
    }

    /// <summary>
    /// EN: Drives the editor progress bar and turns "user pressed Cancel" into an exception at the
    ///     next checkpoint. Progress is reported as a stage name plus a 0..1 fraction.
    /// ZH: 驱动编辑器进度条，并在下一个检查点把"用户按下取消"转换成异常。
    ///     进度以阶段名 + 0..1 的比例形式上报。
    /// </summary>
    public sealed class ATOProgress : IDisposable
    {
        private readonly bool _interactive;
        private string _stage = "";
        private float _fraction;
        private bool _cancelled;
        private double _lastRepaint;

        /// <summary>EN: Cancellation token mirroring the progress bar's cancel button.
        /// ZH: 与进度条取消按钮联动的取消令牌。</summary>
        public CancellationTokenSource TokenSource { get; } = new CancellationTokenSource();

        /// <summary>EN: Create a progress reporter. ZH: 创建进度上报器。</summary>
        /// <param name="interactive">EN: false in batch mode / tests, which disables the UI. ZH: 批处理或测试时为 false，禁用 UI。</param>
        public ATOProgress(bool interactive)
        {
            _interactive = interactive && !UnityEditorInternal.InternalEditorUtility.inBatchMode;
        }

        /// <summary>EN: Update the current stage and fraction, then poll for cancellation.
        /// ZH: 更新当前阶段与进度比例，然后轮询取消状态。</summary>
        public void Report(string stage, float fraction)
        {
            _stage = stage;
            _fraction = UnityEngine.Mathf.Clamp01(fraction);
            Pump(force: true);
        }

        /// <summary>EN: Update only the fraction inside the current stage. ZH: 仅更新当前阶段内的进度比例。</summary>
        public void Report(float fraction)
        {
            _fraction = UnityEngine.Mathf.Clamp01(fraction);
            Pump(force: false);
        }

        /// <summary>
        /// EN: Throw <see cref="ATOCancelledException"/> if the user has cancelled. Safe to call often.
        /// ZH: 若用户已取消则抛出 <see cref="ATOCancelledException"/>。可以频繁调用。
        /// </summary>
        public void ThrowIfCancelled()
        {
            Pump(force: false);
            if (_cancelled) throw new ATOCancelledException();
        }

        private void Pump(bool force)
        {
            if (_cancelled) return;
            if (!_interactive) return;

            var now = EditorApplication.timeSinceStartup;
            // EN: Repainting the progress bar is expensive; throttle to ~20 Hz unless forced.
            // ZH: 重绘进度条开销较大；非强制时限制到约 20 Hz。
            if (!force && now - _lastRepaint < 0.05) return;
            _lastRepaint = now;

            if (EditorUtility.DisplayCancelableProgressBar(ATOConstants.DisplayName, _stage, _fraction))
            {
                _cancelled = true;
                TokenSource.Cancel();
            }
        }

        /// <summary>EN: Clear the progress bar. ZH: 清除进度条。</summary>
        public void Dispose()
        {
            if (_interactive) EditorUtility.ClearProgressBar();
            TokenSource.Dispose();
        }
    }
}
