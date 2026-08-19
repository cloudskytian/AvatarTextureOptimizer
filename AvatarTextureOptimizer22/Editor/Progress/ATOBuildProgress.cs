// AvatarTextureOptimizer
// File: Editor/Progress/ATOBuildProgress.cs
//
// Build progress + cooperative cancellation. The bake runs inside NDMF (editor
// main thread), so cancellation is polled between processing steps; on cancel
// the current step finishes, temporary assets on disk are kept, and CPU/GPU/
// memory resources are released by the caller's dispose path.
//
// 构建进度 + 协作式取消。烘焙运行在 NDMF（编辑器主线程）内，因此取消在
// 处理步骤之间被轮询；取消时当前步骤会执行完，硬盘上的临时资产保留，
// 由调用方的释放路径释放 CPU/GPU/内存资源。

using System;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.progress
{
    /// <summary>
    /// Progress + cancellation state shared across the whole bake.
    /// 整个烘焙共享的进度 + 取消状态。
    /// </summary>
    public sealed class ATOBuildProgress : IDisposable
    {
        private readonly string _avatarName;
        private int _totalSteps;
        private int _currentStep;

        /// <summary>True once the user requests cancellation. / 用户请求取消后为 true。</summary>
        public bool IsCancelled { get; private set; }

        /// <summary>Current step label (localized). / 当前步骤标签（已本地化）。</summary>
        public string CurrentLabel { get; private set; } = "";

        public ATOBuildProgress(string avatarName, int totalSteps)
        {
            _avatarName = avatarName;
            _totalSteps = Mathf.Max(1, totalSteps);
            _currentStep = 0;
            IsCancelled = false;
        }

        /// <summary>Advance to the next step. / 前进到下一步。</summary>
        public void Step(string label, bool cancellable = true)
        {
            if (IsCancelled) return;
            _currentStep++;
            CurrentLabel = label;
            float progress = _currentStep / (float)_totalSteps;
            if (cancellable)
            {
                if (EditorUtility.DisplayCancelableProgressBar(
                        $"[ATO] {_avatarName}",
                        label,
                        progress))
                {
                    IsCancelled = true;
                    logging.ATOLog.Warn("Build cancelled by user / 用户取消了烘焙");
                }
            }
            else
            {
                EditorUtility.DisplayProgressBar($"[ATO] {_avatarName}", label, progress);
            }
        }

        /// <summary>Check-and-throw convenience. / 检查并抛出的便捷方法。</summary>
        public void ThrowIfCancelled()
        {
            if (IsCancelled) throw new ATOBuildCancelledException();
        }

        public void Dispose()
        {
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>
    /// Thrown when the user cancels a bake. The NDMF pass catches this, keeps
    /// temporary assets on disk, and releases resources.
    /// 用户取消烘焙时抛出。NDMF pass 捕获后保留硬盘上的临时资产并释放资源。
    /// </summary>
    public sealed class ATOBuildCancelledException : Exception
    {
        public ATOBuildCancelledException() : base("Avatar Texture Optimizer build cancelled by user") { }
    }
}
