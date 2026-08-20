// SPDX-License-Identifier: MIT
// EN: Cancellable progress reporting.
// ZH: 可取消的进度显示。

using System;
using UnityEditor;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: Thrown when the user cancels the bake. Temporary assets already written to disk are kept,
    ///     but all CPU/GPU/native resources are released by the pipeline's finally blocks.
    /// ZH: 用户取消烘焙时抛出。已经写到硬盘上的临时资产会保留，
    ///     但管线的 finally 块会释放全部 CPU/GPU/原生资源。
    /// </summary>
    public sealed class ATOCancelledException : Exception
    {
        public ATOCancelledException() : base("[ATO] cancelled by user")
        {
        }
    }

    /// <summary>
    /// EN: Wraps <see cref="EditorUtility.DisplayCancelableProgressBar"/> with phase weighting so the bar
    ///     advances smoothly across the whole pipeline.
    /// ZH: 封装 <see cref="EditorUtility.DisplayCancelableProgressBar"/> 并做阶段加权，
    ///     让进度条在整个管线中平滑推进。
    /// </summary>
    public sealed class ATOProgress : IDisposable
    {
        private readonly ATOLog _log;
        private readonly bool _enabled;
        private float _phaseStart;
        private float _phaseEnd;
        private string _phaseName = "";
        private double _lastRepaint;
        private bool _cancelled;

        public ATOProgress(ATOLog log, bool enabled = true)
        {
            _log = log;
            _enabled = enabled;
        }

        /// <summary>EN: True once the user pressed cancel. ZH: 用户点击取消后为 true。</summary>
        public bool Cancelled => _cancelled;

        /// <summary>
        /// EN: Starts a new phase covering [from, to] of the overall progress.
        /// ZH: 开始一个覆盖整体进度 [from, to] 区间的新阶段。
        /// </summary>
        public void BeginPhase(string localisationKey, float from, float to)
        {
            _phaseName = ATOL10n.Tr(localisationKey);
            _phaseStart = from;
            _phaseEnd = to;
            _log.Trace("progress", $"phase '{_phaseName}' [{from:P0} - {to:P0}]");
            Report(0f, null);
        }

        /// <summary>
        /// EN: Reports intra-phase progress in [0,1]; throws if the user cancelled.
        /// ZH: 报告阶段内 [0,1] 的进度；用户取消时抛出异常。
        /// </summary>
        public void Report(float t, string detail)
        {
            if (!_enabled) return;

            var now = EditorApplication.timeSinceStartup;
            if (now - _lastRepaint < 0.05 && t < 1f && !_cancelled) return;
            _lastRepaint = now;

            var overall = _phaseStart + (_phaseEnd - _phaseStart) * Math.Max(0f, Math.Min(1f, t));
            var info = detail == null ? _phaseName : _phaseName + " - " + detail;
            if (EditorUtility.DisplayCancelableProgressBar(ATOL10n.Tr("ato:progress:title"), info, overall))
            {
                _cancelled = true;
            }

            ThrowIfCancelled();
        }

        /// <summary>EN: Throws <see cref="ATOCancelledException"/> if cancelled. ZH: 若已取消则抛出异常。</summary>
        public void ThrowIfCancelled()
        {
            if (_cancelled) throw new ATOCancelledException();
        }

        public void Dispose()
        {
            if (_enabled) EditorUtility.ClearProgressBar();
        }
    }
}
