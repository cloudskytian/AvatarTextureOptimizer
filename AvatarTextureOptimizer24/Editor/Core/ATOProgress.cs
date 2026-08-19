// ============================================================================
// ATOProgress.cs — 进度与取消 / Progress & cancellation
// (EN) Reports build progress via EditorUtility and supports cancellation.
//      On cancel, the build aborts but on-disk temp assets are kept and
//      CPU/GPU/memory resources are released.
// (ZH) 通过 EditorUtility 报告构建进度并支持取消。取消时中止构建，但保留硬盘上
//      的临时资产，并释放 CPU/GPU/内存资源。
// ============================================================================

using System;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public class ATOProgress : IDisposable
    {
        private readonly string _title;
        private readonly float _stageStart;
        private readonly float _stageEnd;
        private bool _cancelled;

        public bool Cancelled => _cancelled;

        /// <summary>(EN) Start a stage progress scope. (ZH) 开始一个阶段进度作用域。</summary>
        public static ATOProgress Stage(string title, int stageIndex, int stageCount)
        {
            float start = stageCount > 0 ? (float)stageIndex / stageCount : 0f;
            float end = stageCount > 0 ? (float)(stageIndex + 1) / stageCount : 1f;
            return new ATOProgress(title, start, end);
        }

        private ATOProgress(string title, float start, float end)
        {
            _title = title;
            _stageStart = start;
            _stageEnd = end;
            Report(0f);
        }

        /// <summary>(EN) Report sub-progress (0..1) within the current stage. (ZH) 报告当前阶段内的子进度 (0..1)。</summary>
        public void Report(float t)
        {
            float p = Mathf.Lerp(_stageStart, _stageEnd, Mathf.Clamp01(t));
            _cancelled = EditorUtility.DisplayCancelableProgressBar(
                "ATO: " + _title,
                $"{_title} ({p * 100f:F0}%)",
                p);
        }

        public void Dispose()
        {
            EditorUtility.ClearProgressBar();
            if (_cancelled)
            {
                // 释放资源 / release resources
                ATOTextureIO.ClearCache();
                GC.Collect();
                throw new OperationCanceledException("[ATO] Build cancelled by user");
            }
        }
    }
}
