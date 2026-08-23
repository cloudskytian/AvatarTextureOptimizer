// Progress bar + cancellation support. / 进度条与取消支持。
// Uses Unity's cancellable progress bar. On cancel we throw OperationCanceledException,
// the pipeline then keeps temporary assets on disk but frees CPU/GPU/memory.
// / 使用 Unity 可取消进度条；取消时抛出 OperationCanceledException，
// 流水线保留硬盘上的临时资产，但释放 CPU/GPU/内存。

using System;
using UnityEditor;

namespace net.fosa.avatar_texture_optimizer.editor.pipeline
{
    /// <summary>
    /// Wraps EditorUtility.DisplayCancelableProgressBar and translates user cancel into an exception.
    /// / 封装 EditorUtility.DisplayCancelableProgressBar，把用户取消转化为异常。
    /// </summary>
    public sealed class ProgressScope : IDisposable
    {
        private readonly string _title;
        private bool _disposed;

        public ProgressScope(string title)
        {
            _title = title;
        }

        /// <summary>Show progress; throws OperationCanceledException if the user cancels. / 显示进度；用户取消时抛出异常。</summary>
        public void Report(string stage, string detail, float fraction)
        {
            if (EditorUtility.DisplayCancelableProgressBar(_title, stage + "\n" + detail, fraction))
            {
                throw new OperationCanceledException("[ATO] Build cancelled by user. Temporary assets were kept; CPU/GPU/memory released. / 用户取消构建：临时资产已保留，资源已释放。");
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
