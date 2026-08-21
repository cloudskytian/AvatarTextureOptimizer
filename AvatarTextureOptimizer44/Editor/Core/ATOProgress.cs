// ATOProgress.cs - Stage/progress display with cancellation. / 阶段进度显示与取消支持。
// Cancel keeps on-disk temp assets but stops the work and releases CPU/GPU/memory.
// 取消时保留硬盘临时资产，但停止工作并释放 CPU/GPU/内存。
using System;
using UnityEditor;
using UnityEngine;

namespace Fosa.ATO.Editor.Core
{
    /// <summary>Thrown when the user cancels. / 用户取消时抛出。</summary>
    public class ATOCancelledException : OperationCanceledException
    {
        public ATOCancelledException() : base("[ATO] Cancelled by user / 已被用户取消") { }
    }

    /// <summary>Progress reporter + cancel pump. / 进度上报与取消泵。</summary>
    public sealed class ATOProgress : IDisposable
    {
        private readonly string _title;
        private float _scopeBase, _scopeSpan;
        private string _stage = "";
        private double _lastPaint;

        public ATOProgress(string title) { _title = title; EditorApplication.LockReloadAssemblies(); }

        /// <summary>Begin a sub scope mapped onto [base, base+span]. / 开始映射到全局区间内的子阶段。</summary>
        public ATOProgress Sub(float base01, float span01) { var p = new ATOProgress(_title); p._scopeBase = base01; p._scopeSpan = span01; return p; }

        /// <summary>Report progress; throws ATOCancelledException when user cancels. / 上报进度；用户取消时抛出异常。</summary>
        public void Report(float local01, string stage)
        {
            if (string.IsNullOrEmpty(stage)) stage = _stage;
            _stage = stage;
            float global = UnityEngine.Mathf.Clamp01(_scopeBase + _scopeSpan * Mathf.Clamp01(local01));
            // throttle repaint to ~10fps / 重绘限频约10fps（取消按钮在每次重绘时可点击）
            if (EditorApplication.timeSinceStartup - _lastPaint < 0.1 && global < 1f) return;
            _lastPaint = EditorApplication.timeSinceStartup;
            // DisplayCancelableProgressBar is the only reliable cancel UI during ndmf builds / 构建期唯一可靠的取消UI
            if (EditorUtility.DisplayCancelableProgressBar(_title, $"{stage} ({(int)(global * 100)}%)", global))
                throw new ATOCancelledException();
        }

        /// <summary>Clear the bar. / 清除进度条。</summary>
        public void Clear() => EditorUtility.ClearProgressBar();

        public void Dispose() { EditorUtility.ClearProgressBar(); EditorApplication.UnlockReloadAssemblies(); }
    }
}
