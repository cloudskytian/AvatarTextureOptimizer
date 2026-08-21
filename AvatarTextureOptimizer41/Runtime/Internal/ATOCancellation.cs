using System;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif

// Cooperative cancellation for long bakes.
// 长时烘焙的协作式取消。

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Cooperative cancellation token used across the pipeline. Polls the editor progress bar's
    /// cancel button and aborts when compilation starts. On cancel, GPU/CPU resources are released;
    /// temporary assets are intentionally kept on disk (per requirements).
    /// 协作式取消令牌：轮询编辑器进度条取消按钮，并在编译开始时中止。取消时释放 GPU/CPU 资源，
    /// 临时资产按需求保留在硬盘上。
    /// </summary>
    public sealed class ATOCancellation
    {
        public bool IsCancelled { get; private set; }
        public string Stage { get; private set; } = "";

        public void Cancel() => IsCancelled = true;

        /// <summary>
        /// Call periodically inside loops. Shows/updates the progress bar; returns true when cancelled.
        /// 在循环中周期性调用：显示/更新进度条，被取消时返回 true。
        /// </summary>
        public bool Check(string stage, float progress01)
        {
            Stage = stage;
            if (IsCancelled) return true;
#if UNITY_EDITOR
            // Abort if the user starts a compilation while we are baking. 烘焙期间用户触发编译则中止。
            if (EditorApplication.isCompiling)
            {
                IsCancelled = true;
                return true;
            }
            if (EditorUtility.DisplayCancelableProgressBar("AvatarTextureOptimizer", stage, progress01))
            {
                IsCancelled = true;
                return true;
            }
#endif
            return false;
        }

        public void ThrowIfCancelled(string stage, float progress01)
        {
            if (Check(stage, progress01))
                throw new OperationCanceledException("AvatarTextureOptimizer cancelled by user");
        }

        public void Clear()
        {
            IsCancelled = false;
#if UNITY_EDITOR
            EditorUtility.ClearProgressBar();
#endif
        }
    }
}
