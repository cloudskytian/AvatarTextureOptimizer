// SPDX-License-Identifier: MIT
// EN: Cancellable progress reporting backed by the editor progress bar.
// ZH: 基于编辑器进度条、可取消的进度报告。

using System;
using UnityEditor;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Core
{
    /// <summary>
    /// EN: Thrown when the user cancels the bake. The pipeline catches it, releases CPU/GPU resources and
    ///     aborts the build while leaving already written temporary assets on disk.
    /// ZH: 用户取消烘焙时抛出。管线会捕获它、释放 CPU/GPU 资源并中止构建，
    ///     同时保留已经写入硬盘的临时资产。
    /// </summary>
    public sealed class AtoCancelledException : OperationCanceledException
    {
        /// <summary>EN: Creates the exception. ZH: 创建该异常。</summary>
        public AtoCancelledException() : base("[ATO] Build cancelled by the user.") { }
    }

    /// <summary>
    /// EN: Progress reporter. Stages advertise a weight so the overall bar advances smoothly, and every
    ///     <see cref="Step"/> call gives the user a chance to cancel.
    /// ZH: 进度报告器。各阶段声明权重以让总进度条平滑推进，
    ///     每次调用 <see cref="Step"/> 都给用户一次取消的机会。
    /// </summary>
    public sealed class AtoProgress : IDisposable
    {
        private readonly string _title;
        private readonly bool _interactive;
        private float _stageStart;
        private float _stageEnd;
        private string _stageLabel = "";
        private bool _shown;
        private double _lastRepaint;

        /// <summary>EN: True once the user pressed cancel. ZH: 用户按下取消后为 true。</summary>
        public bool Cancelled { get; private set; }

        /// <summary>
        /// EN: Creates a reporter. Pass <paramref name="interactive"/> = false for automated builds where
        ///     a modal progress bar would be inappropriate.
        /// ZH: 创建报告器。自动化构建中不适合弹出模态进度条时，将 <paramref name="interactive"/> 设为 false。
        /// </summary>
        public AtoProgress(string title, bool interactive = true)
        {
            _title = title;
            _interactive = interactive;
        }

        /// <summary>
        /// EN: Declares the normalized [0,1] slice of the overall bar owned by the next stage.
        /// ZH: 声明下一个阶段在总进度条上占据的归一化 [0,1] 区间。
        /// </summary>
        public void BeginStage(string label, float from, float to)
        {
            _stageLabel = label;
            _stageStart = from;
            _stageEnd = to;
            Step(0f, label);
        }

        /// <summary>
        /// EN: Advances within the current stage. <paramref name="t"/> is the stage-local progress in [0,1].
        ///     Throws <see cref="AtoCancelledException"/> if the user cancelled.
        /// ZH: 在当前阶段内推进。<paramref name="t"/> 是阶段内的 [0,1] 进度。
        ///     若用户已取消则抛出 <see cref="AtoCancelledException"/>。
        /// </summary>
        public void Step(float t, string detail = null)
        {
            if (Cancelled) throw new AtoCancelledException();
            if (!_interactive) return;

            // EN: Repainting the progress bar is expensive; throttle to ~15 Hz.
            // ZH: 重绘进度条开销较大；限制到约 15 Hz。
            var now = EditorApplication.timeSinceStartup;
            if (_shown && now - _lastRepaint < 0.066) return;
            _lastRepaint = now;
            _shown = true;

            var global = _stageStart + (_stageEnd - _stageStart) * UnityEngine.Mathf.Clamp01(t);
            var info = detail == null ? _stageLabel : $"{_stageLabel} - {detail}";
            if (EditorUtility.DisplayCancelableProgressBar(_title, info, global))
            {
                Cancelled = true;
                AtoLog.Warning("Progress", "Cancellation requested by the user.");
                throw new AtoCancelledException();
            }
        }

        /// <summary>EN: Clears the progress bar. ZH: 清除进度条。</summary>
        public void Dispose()
        {
            if (_shown) EditorUtility.ClearProgressBar();
            _shown = false;
        }
    }
}
