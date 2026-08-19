// ATO — Avatar Texture Optimizer
// Base class for all pipeline passes: shared context access, stage timing and
// cancellation-safe execution.
// 所有管线 Pass 的基类：共享上下文访问、阶段计时与可取消的安全执行。

using System;
using nadena.dev.ndmf;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Base pass. Derived classes implement <see cref="Process"/>. 基类 Pass，子类实现 <see cref="Process"/>。
    /// </summary>
    public abstract class ATOBasePass<T> : Pass<T> where T : Pass<T>, new()
    {
        /// <summary>The per-build context. 单次构建上下文。</summary>
        protected ATOBuildContext GetBuild(BuildContext ctx) => ctx.GetState<ATOBuildContext>();

        protected override sealed void Execute(BuildContext context)
        {
            var bc = GetBuild(context);
            try
            {
                Process(bc, context);
            }
            catch (ATOBuildCancelledException)
            {
                ATOLog.Warn(ATOI18n.T(ATOI18nKeys.ProgressCancelled));
                // Re-throw would show as an error; instead swallow and let NDMF continue with a
                // partially-processed avatar is unsafe. We re-throw a normal exception so the build
                // is aborted but the message is clean.
                // 重新抛出会显示为错误；为安全起见中止构建，但保持消息干净。
                throw new Exception(ATOI18n.T(ATOI18nKeys.ProgressCancelled));
            }
            finally
            {
                // Always release transient GPU/CPU resources held by this pass. 始终释放该 Pass 持有的临时资源。
                ReleaseResources(bc);
                context.EndStageSafe();
            }
        }

        /// <summary>Pass body. Pass 主体。</summary>
        protected abstract void Process(ATOBuildContext bc, BuildContext context);

        /// <summary>
        /// Override to release transient resources (RenderTextures, native arrays) after the pass.
        /// 重写以在 Pass 结束后释放临时资源（RenderTexture、NativeArray）。
        /// </summary>
        protected virtual void ReleaseResources(ATOBuildContext bc) { }

        /// <summary>
        /// Run a named stage, timing it and reporting progress. 运行命名阶段，计时并报告进度。
        /// </summary>
        protected void RunStage(ATOBuildContext bc, string i18nStageKey, int totalUnits, Action action)
        {
            bc.ThrowIfCancelled();
            bc.BeginStage(i18nStageKey, totalUnits);
            var timer = new ATOLog.StageTimer($"[ATO] {ATOI18n.T(i18nStageKey)}");
            try
            {
                action();
            }
            finally
            {
                double ms = timer.Stop();
                bc.Report.AddStage(i18nStageKey, ms);
                bc.EndStage();
            }
        }
    }

    /// <summary>
    /// Small extension for safe progress-bar cleanup. 安全清理进度条的扩展。
    /// </summary>
    internal static class BuildContextExtensions
    {
        public static void EndStageSafe(this BuildContext ctx)
        {
            // NDMF does not own the progress bar; ensure it is cleared if we left one up.
            // NDMF 不管理进度条；确保退出时清除。
            try { UnityEditor.EditorUtility.ClearProgressBar(); }
            catch (Exception) { /* ignore 忽略 */ }
        }
    }
}
