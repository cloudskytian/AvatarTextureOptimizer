// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Cancellable progress reporting.
// AvatarTextureOptimizer (ATO) - 可取消的进度显示。

using System;
using UnityEditor;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Core
{
    /// <summary>
    /// EN: Thrown when the user cancels the bake. The main pass catches it, releases every GPU/CPU/native
    ///     resource it owns and aborts the build. Temporary assets already written to disk are intentionally
    ///     left in place (NDMF cleans them up on the next successful build).
    /// ZH: 用户取消烘焙时抛出。主 Pass 会捕获它，释放自己持有的全部 GPU/CPU/原生资源并中止构建。
    ///     已写入硬盘的临时资产会被刻意保留（NDMF 会在下次成功构建时清理）。
    /// </summary>
    public sealed class ATOCancelledException : OperationCanceledException
    {
        public ATOCancelledException() : base("[ATO] Build cancelled by user") { }
    }

    /// <summary>
    /// EN: Coarse two-level progress: a stage weight table plus intra-stage fractions, so the bar advances
    ///     monotonically and predictably rather than jumping around.
    /// ZH: 两级进度：阶段权重表 + 阶段内部比例，使进度条单调、可预期地推进，而不会来回跳动。
    /// </summary>
    public sealed class ATOProgress : IDisposable
    {
        private readonly string _title;
        private readonly bool _interactive;
        private float _stageStart;
        private float _stageEnd;
        private string _stageLabel = "";
        private bool _cancelled;
        private double _lastRepaint;

        public ATOProgress(string title, bool interactive = true)
        {
            _title = title;
            _interactive = interactive && !UnityEngine.Application.isBatchMode;
        }

        /// <summary>EN: Begin a weighted stage covering [from, to] of the overall bar. ZH: 开始一个占总进度 [from, to] 的阶段。</summary>
        public void BeginStage(string label, float from, float to)
        {
            _stageLabel = label;
            _stageStart = from;
            _stageEnd = to;
            Report(0f, null);
        }

        /// <summary>EN: Report intra-stage progress in [0,1]. Throws if the user cancelled.
        ///     ZH: 汇报阶段内 [0,1] 的进度。若用户点了取消则抛出异常。</summary>
        public void Report(float fraction, string detail)
        {
            if (_cancelled) throw new ATOCancelledException();
            if (!_interactive) return;

            // EN: Repainting the progress bar is expensive; throttle to ~20 fps.
            // ZH: 重绘进度条开销较大，限制在约 20fps。
            var now = EditorApplication.timeSinceStartup;
            if (fraction > 0f && fraction < 1f && now - _lastRepaint < 0.05) return;
            _lastRepaint = now;

            fraction = UnityEngine.Mathf.Clamp01(fraction);
            var overall = UnityEngine.Mathf.Lerp(_stageStart, _stageEnd, fraction);
            var info = string.IsNullOrEmpty(detail) ? _stageLabel : $"{_stageLabel} - {detail}";

            if (EditorUtility.DisplayCancelableProgressBar(_title, info, overall))
            {
                _cancelled = true;
                throw new ATOCancelledException();
            }
        }

        /// <summary>EN: Convenience for loops. ZH: 循环内的便捷调用。</summary>
        public void Report(int index, int count, string detail = null)
        {
            Report(count <= 0 ? 1f : (float)index / count, detail);
        }

        public void ThrowIfCancelled()
        {
            if (_cancelled) throw new ATOCancelledException();
        }

        public void Dispose()
        {
            if (_interactive) EditorUtility.ClearProgressBar();
        }
    }
}
