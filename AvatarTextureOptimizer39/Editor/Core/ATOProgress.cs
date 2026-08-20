// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System;
using UnityEditor;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Progress + cancellation reporting for a bake. Shows the current stage with a
    /// cancelable progress bar. Cancelling stops the bake, keeps temp assets on disk,
    /// and releases CPU/GPU/memory resources.
    ///
    /// 烘焙进度与取消报告。显示当前阶段，并提供可取消的进度条。取消时终止烘焙、
    /// 保留硬盘临时资产，并释放 CPU/GPU/内存资源。
    /// </summary>
    public sealed class ATOProgress : IDisposable
    {
        private readonly ATOBuildState _state;
        private readonly string _avatarName;

        public ATOProgress(ATOBuildState state, string avatarName)
        {
            _state = state;
            _avatarName = avatarName;
        }

        private int _totalStages = 1;
        private int _stage = 0;
        private string _stageName = "";

        /// <summary>Total number of stages for the progress bar. 进度条总阶段数。</summary>
        public void SetTotalStages(int total) { _totalStages = Mathf.Max(1, total); }

        /// <summary>
        /// Begin a named stage. Throws OperationCanceledException if cancelled.
        /// 开始一个命名阶段。若已取消则抛异常。
        /// </summary>
        public void BeginStage(string stageName)
        {
            _stageName = stageName;
            ATOLog.Step($"{stageName} ({_stage + 1}/{_totalStages})");
            Report();
        }

        /// <summary>Advance to the next stage. 进入下一阶段。</summary>
        public void NextStage()
        {
            _stage++;
            Report();
        }

        /// <summary>
        /// Update progress within the current stage (0..1). 更新当前阶段内进度（0..1）。
        /// </summary>
        public void Report(float fraction = 0f)
        {
            _state.ThrowIfCancelled();

            float f = (_stage + Mathf.Clamp01(fraction)) / _totalStages;
            bool cancel = EditorUtility.DisplayCancelableProgressBar(
                $"ATO: {_avatarName}", _stageName, f);

            if (cancel)
            {
                _state.Cancelled = true;
                EditorUtility.ClearProgressBar();
                throw new OperationCanceledException(
                    "[ATO] Bake cancelled by user; temp assets kept, resources released. / " +
                    "用户取消了烘焙；临时资产已保留，资源已释放。");
            }
        }

        public void Dispose()
        {
            EditorUtility.ClearProgressBar();
        }
    }
}
