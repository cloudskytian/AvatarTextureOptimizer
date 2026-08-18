// Avatar Texture Optimizer (ATO)
// Cooperative progress reporting + cancellation.
// 协作式进度上报与取消。

using System;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Reports per-stage progress to the console and supports cooperative cancellation.
    /// On cancel: terminates the build, keeps temp assets on disk, but releases CPU/GPU/memory.
    /// 向控制台上报各阶段进度并支持协作取消。取消时：终止烘焙，保留硬盘上的临时资产，
    /// 但释放 CPU/GPU/内存资源。
    /// </summary>
    public sealed class ATOProgress : IDisposable
    {
        private readonly string _stagePrefix;
        private readonly System.Diagnostics.Stopwatch _sw;
        private int _totalSteps;
        private int _currentStep;
        private volatile bool _cancelRequested;

        public ATOProgress(string stagePrefix)
        {
            _stagePrefix = stagePrefix;
            _sw = System.Diagnostics.Stopwatch.StartNew();
        }

        public bool IsCancellationRequested => _cancelRequested;

        public void Cancel()
        {
            _cancelRequested = true;
            ATOLogger.Warn($"Cancellation requested during '{_stagePrefix}'. Releasing resources. / 已在 '{_stagePrefix}' 阶段请求取消，正在释放资源。");
        }

        public void Begin(int totalSteps)
        {
            _totalSteps = Mathf.Max(1, totalSteps);
            _currentStep = 0;
            ATOLogger.Info($"=== [ATO] {_stagePrefix} started ({_totalSteps} steps) ===");
        }

        public void Advance(int by = 1, string detail = null)
        {
            _currentStep += by;
            if (detail != null) ATOLogger.Debug($"{_stagePrefix} [{_currentStep}/{_totalSteps}] {detail}");
        }

        /// <summary>Throw if cancelled. Call frequently inside long loops. / 若已取消则抛出，用于长循环内频繁检查。</summary>
        public void ThrowIfCancelled()
        {
            if (_cancelRequested)
            {
                ReleaseResources();
                throw new OperationCanceledException($"ATO build cancelled during '{_stagePrefix}'");
            }
        }

        private void ReleaseResources()
        {
            // Release any pooled GPU temporaries we may hold. / 释放可能持有的 GPU 临时资源。
            try
            {
                ATOGpu.ReleaseAll();
            }
            catch (Exception e)
            {
                ATOLogger.Warn("Failed to release GPU resources on cancel: " + e.Message);
            }
        }

        public void End()
        {
            _sw.Stop();
            ATOLogger.Info($"=== [ATO] {_stagePrefix} finished in {_sw.Elapsed.TotalSeconds:F2}s ===");
        }

        public void Dispose()
        {
            ReleaseResources();
        }
    }
}
