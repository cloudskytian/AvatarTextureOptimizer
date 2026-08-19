using System;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    // 用户取消烘焙。Thrown when the user cancels the build.
    internal sealed class ATOCancelledException : Exception
    {
        public ATOCancelledException() : base("Build cancelled by user. 用户取消了构建。") { }
    }

    // 配置错误，必须中止烘焙/构建。Configuration error: the build must be aborted.
    internal sealed class ATOAbortException : Exception
    {
        public ATOAbortException(string message) : base(message) { }
    }

    // 进度显示与取消支持。
    // Progress display and cancellation support.
    // 取消策略：抛出 ATOCancelledException 终止烘焙；硬盘上的临时资产保留（不删除），
    // CPU/GPU/内存资源随异常栈展开与 finally 块释放。
    // Cancellation policy: ATOCancelledException aborts the build; temporary assets on disk are kept,
    // CPU/GPU/memory resources are released via stack unwinding and finally blocks.
    internal static class ATOCancellation
    {
        private static bool _requested;

        public static bool Requested => _requested;

        public static void Reset()
        {
            _requested = false;
        }

        public static void Request()
        {
            _requested = true;
        }

        // 更新进度条；返回是否被取消。Updates the progress bar; returns whether cancellation was requested.
        public static bool Update(string title, string info, float progress01)
        {
            if (UnityEditor.EditorUtility.DisplayCancelableProgressBar(title, info, progress01))
            {
                _requested = true;
            }
            return _requested;
        }

        public static void End()
        {
            UnityEditor.EditorUtility.ClearProgressBar();
        }
    }
}
