// ============================================================================
// ATO - build session: progress + cancellation
// ATO - 构建会话：进度与取消
//
// NDMF 1.14 exposes no cancellation hook, so ATO implements its own session:
// a lightweight progress state visible to a dedicated editor window (stage +
// percent + Cancel button). Cancellation is cooperative: the pipeline checks
// checkpoints at stage boundaries and inside long loops. On cancel the
// pipeline releases CPU/GPU/memory resources, leaves the avatar UNTOUCHED
// (all Unity-object mutations happen in the final Apply stage), keeps the
// temporary assets on disk, and throws ATOPipelineCancelledException which
// surfaces in the NDMF error report so VRChat builds fail loudly instead of
// shipping a half-optimized avatar.
// NDMF 1.14 未提供取消钩子，因此 ATO 自实现会话：一个可供专用编辑器窗口显示
// 的轻量进度状态（阶段 + 百分比 + 取消按钮）。取消为协作式：管线在阶段边界
// 与长循环内检查检查点。取消时管线释放 CPU/GPU/内存资源、保持 Avatar 未被修
// 改（所有 Unity 对象改动都发生在最后的 Apply 阶段）、保留磁盘上的临时资产，
// 并抛出 ATOPipelineCancelledException 使其出现在 NDMF 错误报告中——这样
// VRChat 构建会明确失败，而不是产出半成品。
// ============================================================================

#region

using System;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Core
{
    /// <summary>Thrown to abort the ATO pipeline after a user cancel.
    /// 用户取消后用于中止 ATO 管线的异常。</summary>
    public class ATOPipelineCancelledException : OperationCanceledException
    {
        public ATOPipelineCancelledException()
            : base("ATO pipeline was cancelled by the user. " +
                   "The avatar was left unmodified and temporary assets were kept on disk. " +
                   "ATO 管线已被用户取消。Avatar 保持未修改，临时资产已保留在磁盘上。")
        {
        }
    }

    /// <summary>Per-build session state (progress + cancellation).
    /// 每次构建的会话状态（进度+取消）。</summary>
    public sealed class ATOBuildSession : IDisposable
    {
        public int StageIndex;
        public int StageCount;
        public string StageName = "";
        /// <summary>0..1 within the current stage. 当前阶段内进度。</summary>
        public float StageProgress;
        public bool CancelRequested;

        /// <summary>Raised on any state change (UI refresh).
        /// 状态变化时触发（UI 刷新）。</summary>
        public event Action Changed;

        public void SetStage(int index, int count, string name)
        {
            StageIndex = index;
            StageCount = count;
            StageName = name;
            StageProgress = 0f;
            Changed?.Invoke();
        }

        public void SetProgress(float p)
        {
            StageProgress = Mathf.Clamp01(p);
            Changed?.Invoke();
        }

        public void RequestCancel()
        {
            if (!CancelRequested)
            {
                CancelRequested = true;
                Changed?.Invoke();
            }
        }

        /// <summary>Checkpoint: throws when cancellation was requested.
        /// 检查点：请求取消时抛出。</summary>
        public void Check(string stageName)
        {
            if (CancelRequested)
            {
                Debug.Log($"[ATO] Cancel requested during \"{stageName}\" - aborting pipeline at next safe point. " +
                          "取消请求于 “{stageName}” 期间发出 - 将在下一个安全点中止管线。");
                throw new ATOPipelineCancelledException();
            }
        }

        public void Dispose()
        {
            Changed = null;
        }
    }
}
