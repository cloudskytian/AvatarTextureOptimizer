using UnityEditor;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Build progress + cancellation. Uses Unity's cancelable progress bar; cancelling throws
    /// <see cref="AtoCancelledException"/> which aborts the build. Temporary assets already saved
    /// stay on disk; the processor's finally blocks release CPU/GPU/memory resources.
    /// / 构建进度与取消：使用 Unity 可取消进度条；取消时抛出异常中止构建。
    /// 已保存的临时资产保留在磁盘，finally 中释放 CPU/GPU/内存资源。
    /// </summary>
    internal static class ATOProgress
    {
        private const string Title = "Avatar Texture Optimizer";
        private static bool _active;

        internal static void Begin()
        {
            _active = true;
        }

        internal static void End()
        {
            if (_active)
            {
                EditorUtility.ClearProgressBar();
                _active = false;
            }
        }

        /// <summary>
        /// Report progress. Throws on cancel. `overall` is 0..1.
        /// / 汇报进度（0..1）；用户点取消则抛异常。
        /// </summary>
        internal static void Report(float overall, string stage, string detail = null)
        {
            if (!_active) return;
            var msg = string.IsNullOrEmpty(detail) ? stage : $"{stage} — {detail}";
            if (EditorUtility.DisplayCancelableProgressBar(Title, msg, UnityEngine.Mathf.Clamp01(overall)))
            {
                throw new AtoCancelledException();
            }
        }
    }
}
