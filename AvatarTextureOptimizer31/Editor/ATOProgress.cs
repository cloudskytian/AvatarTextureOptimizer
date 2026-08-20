// ATOProgress.cs
// Progress reporting and cancellation support using EditorUtility.DisplayCancelableProgressBar.
// Shows current phase and progress percentage. Allows user to cancel the build.
// 使用 EditorUtility.DisplayCancelableProgressBar 的进度报告与取消支持。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Manages the cancellable progress bar for the ATO pipeline.
    /// Throws OperationCanceledException when the user cancels.
    /// 管理 ATO 管线的可取消进度条。
    /// </summary>
    internal sealed class ATOProgress : IDisposable
    {
        private const string Title = "Avatar Texture Optimizer";
        private bool _disposed;

        /// <summary>
        /// Updates the progress bar. Throws OperationCanceledException if user clicked Cancel.
        /// 更新进度条。用户点击取消时抛出 OperationCanceledException。
        /// </summary>
        internal void Update(float progress, string info)
        {
            if (_disposed) return;

            if (EditorUtility.DisplayCancelableProgressBar(Title, info, Mathf.Clamp01(progress)))
            {
                // User clicked Cancel
                Cancel();
                throw new OperationCanceledException("ATO: User cancelled the optimization.");
            }
        }

        /// <summary>
        /// Shows the current phase with a progress percentage.
        /// 显示当前阶段及进度百分比。
        /// </summary>
        internal void ShowPhase(string phaseName, int currentPhase, int totalPhases)
        {
            float progress = totalPhases > 0 ? (float)currentPhase / totalPhases : 0f;
            Update(progress, $"Phase {currentPhase}/{totalPhases}: {phaseName}");
        }

        /// <summary>
        /// Shows a sub-step progress within a phase.
        /// 显示阶段内的子步骤进度。
        /// </summary>
        internal void ShowSubStep(string phaseName, int currentPhase, int totalPhases,
            int currentStep, int totalSteps, string detail)
        {
            float phaseProgress = totalPhases > 0 ? (float)(currentPhase - 1) / totalPhases : 0f;
            float subProgress = totalSteps > 0 ? (float)currentStep / totalSteps / totalPhases : 0f;
            float totalProgress = phaseProgress + subProgress;
            string info = $"Phase {currentPhase}/{totalPhases}: {phaseName} ({currentStep}/{totalSteps})";
            if (!string.IsNullOrEmpty(detail))
                info += $" - {detail}";
            Update(totalProgress, info);
        }

        /// <summary>
        /// Cancels the operation: clears the progress bar and releases resources.
        /// 取消操作：清除进度条并释放资源。
        /// </summary>
        internal void Cancel()
        {
            EditorUtility.ClearProgressBar();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EditorUtility.ClearProgressBar();
        }
    }
}
