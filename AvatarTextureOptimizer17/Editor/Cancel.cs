// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Cancel.cs — 构建取消支持 / Cooperative build cancellation
//
// 需求: 烘焙或构建时显示当前阶段与进度并支持取消；取消时终止烘焙或构建，
//       保留硬盘上的临时资产，但释放 CPU/GPU/内存资源。
// 共识: NDMF 本身不提供取消机制 → 用 EditorUtility.DisplayCancelableProgressBar +
//       协作式检查点（每次循环/任务前检查）。取消时抛出 ATOCancelException，
//       顶层捕获后释放 GPU/CPU 资源并停止。
// ============================================================================
using System;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 取消异常 / Thrown when the user cancels a bake.
    /// </summary>
    public sealed class ATOCancelException : Exception
    {
        public ATOCancelException() : base("AvatarTextureOptimizer build cancelled by user") { }
    }

    /// <summary>
    /// 取消令牌与进度条 / Cancellation token + progress bar.
    /// </summary>
    public static class Cancel
    {
        private static bool _cancelled;
        private static string _title = "";

        /// <summary>是否已请求取消 / Whether cancellation was requested</summary>
        public static bool IsCancelled => _cancelled;

        /// <summary>重置状态（每次构建开始） / Reset per build</summary>
        public static void Reset()
        {
            _cancelled = false;
        }

        /// <summary>
        /// 显示进度并检查取消；用户点取消则置位标志 /
        /// Shows progress and checks cancellation; sets the flag if the user cancels.
        /// </summary>
        public static void Tick(string stage, float progress01)
        {
            if (_title.Length == 0) _title = "AvatarTextureOptimizer";
            if (EditorUtility.DisplayCancelableProgressBar(_title, stage, Mathf.Clamp01(progress01)))
            {
                _cancelled = true;
            }
        }

        /// <summary>
        /// 协作式检查点：已取消则抛 ATOCancelException /
        /// Cooperative checkpoint: throws ATOCancelException if cancelled.
        /// </summary>
        public static void Checkpoint()
        {
            if (_cancelled) throw new ATOCancelException();
        }

        /// <summary>清除进度条 / Clear the progress bar</summary>
        public static void Clear()
        {
            EditorUtility.ClearProgressBar();
        }
    }
}
