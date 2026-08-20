using System;
using System.Collections.Generic;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Central runtime resource cache. / 运行时资源缓存中心。
    ///
    /// All CPU/GPU resources acquired during the build (pixel buffers, RenderTextures, native
    /// arrays, raster masks) are registered here so that cancellation or errors can release
    /// them all at once — no leaks, low memory footprint. /
    /// 构建期间获取的全部 CPU/GPU 资源（像素缓冲、RenderTexture、NativeArray、光栅掩码）都注册到这里，
    /// 取消或出错时可一次性释放 —— 不泄漏、内存占用低。
    /// </summary>
    internal static class AtoRuntimeCache
    {
        private static readonly List<Action> CleanupActions = new List<Action>();

        /// <summary>
        /// Register a cleanup action (e.g. releasing a RenderTexture). / 注册清理动作（如释放 RenderTexture）。
        /// </summary>
        public static void Track(Action cleanup)
        {
            if (cleanup == null) return;
            lock (CleanupActions)
            {
                CleanupActions.Add(cleanup);
            }
        }

        /// <summary>
        /// Release all tracked resources and clear the list. Idempotent. / 释放全部已跟踪资源并清空列表（幂等）。
        /// </summary>
        public static void ReleaseAll()
        {
            lock (CleanupActions)
            {
                for (var i = CleanupActions.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        CleanupActions[i]?.Invoke();
                    }
                    catch (Exception e)
                    {
                        AtoLog.Verbose($"cache cleanup failed: {e.Message}");
                    }
                }
                CleanupActions.Clear();
            }
        }
    }
}
