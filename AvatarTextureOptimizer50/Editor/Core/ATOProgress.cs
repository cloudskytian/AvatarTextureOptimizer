// -----------------------------------------------------------------------------
// ATOProgress.cs — stage/progress display with cancellation & per-stage timing.
// ATOProgress.cs — 阶段进度显示、取消支持与阶段计时。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>Thrown when the user cancels the bake. Resources are released by the pass's finally block.
    /// 用户取消烘焙时抛出；资源由 Pass 的 finally 块释放。</summary>
    internal sealed class ATOCancelledException : OperationCanceledException
    {
        public ATOCancelledException() : base("ATO bake cancelled by user / ATO 烘焙已被用户取消") { }
    }

    /// <summary>
    /// Progress bar + stage timer. Cancel keeps temp assets on disk (ATO writes none by default)
    /// and releases CPU/GPU/memory via the caller's finally.
    /// 进度条+阶段计时。取消时保留硬盘临时资产（ATO 默认不写盘），CPU/GPU/内存由调用方 finally 释放。
    /// </summary>
    internal sealed class ATOProgress : IDisposable
    {
        private readonly string _title = "Avatar Texture Optimizer";
        private readonly Stopwatch _total = new Stopwatch();
        private readonly Stopwatch _stage = new Stopwatch();
        private readonly List<(string stage, double ms)> _stageTimings = new List<(string, double)>();

        private string _stageName = "";
        private float _stageStart;   // overall fraction at stage start / 阶段起点的总进度
        private float _stageSpan;    // fraction this stage may consume / 本阶段占用的进度跨度

        public IReadOnlyList<(string stage, double ms)> StageTimings => _stageTimings;

        public ATOProgress() { _total.Start(); }

        /// <summary>Begin a named stage with its share of overall progress.
        /// 开始一个阶段，并声明其占总进度的跨度。</summary>
        public void BeginStage(string name, float startFraction, float spanFraction)
        {
            CommitStage();
            _stageName = name;
            _stageStart = Mathf.Clamp01(startFraction);
            _stageSpan = Mathf.Clamp01(spanFraction);
            _stage.Restart();
            ATOLog.Info($"── Stage ▶ {name} / stage start ──");
            Set(0, $"{name} ...");
        }

        /// <summary>Update progress within the current stage (0..1). Throws on cancel.
        /// 更新当前阶段内进度（0..1）。取消时抛出异常。</summary>
        public void Report(float localT, string detail = null)
        {
            Set(Mathf.Clamp01(localT), detail);
        }

        private void Set(float localT, string detail)
        {
            float overall = Mathf.Clamp01(_stageStart + _stageSpan * Mathf.Clamp01(localT));
            string msg = string.IsNullOrEmpty(detail) ? _stageName : $"{_stageName} — {detail}";
            try
            {
                if (EditorUtility.DisplayCancelableProgressBar(_title, msg, overall))
                    throw new ATOCancelledException();
            }
            catch (ATOCancelledException)
            {
                throw;
            }
            catch (Exception)
            {
                // No UI available (batch mode etc.) — continue silently.
                // 无 UI（批处理等）时静默继续。
            }
        }

        /// <summary>Finish the current stage, recording its duration. / 结束当前阶段并记录耗时。</summary>
        public void CommitStage()
        {
            if (_stage.IsRunning)
            {
                _stage.Stop();
                _stageTimings.Add((_stageName, _stage.Elapsed.TotalMilliseconds));
                ATOLog.Info($"── Stage ◀ {_stageName}: {_stage.Elapsed.TotalMilliseconds:F1} ms ──");
            }

            _stage.Reset();
        }

        public double TotalMs
        {
            get { return _total.Elapsed.TotalMilliseconds; }
        }

        public void Dispose()
        {
            CommitStage();
            _total.Stop();
            try { EditorUtility.ClearProgressBar(); } catch (Exception) { }
        }
    }
}
