using System;
using System.Diagnostics;
using UnityEditor;

namespace Fosa.AvatarTextureOptimizer.Editor.Pipeline
{
    /// <summary>
    /// Main-thread progress/cancellation scope. Deep algorithms may call Checkpoint freely; it is a no-op outside
    /// ATOPipeline and throttles Editor progress-bar updates to avoid turning tight loops into IMGUI bottlenecks.
    /// 主线程进度/取消作用域；流水线之外不生效，并对深层循环的 UI 更新节流。
    /// </summary>
    internal static class ATOProgress
    {
        private const long RefreshMilliseconds = 75;
        private static readonly Stopwatch Refresh = new Stopwatch();
        private static bool _active;
        private static Func<bool> _cancellationProbe;
        private static string _phase = string.Empty;
        private static string _detail = string.Empty;
        private static float _progress;

        public static void Begin(Func<bool> cancellationProbe = null)
        {
            if (_active) throw new InvalidOperationException("ATO progress scope is already active.");
            _active = true; _cancellationProbe = cancellationProbe; Refresh.Restart();
        }

        public static void Show(string phase, string detail, float progress)
        {
            _phase = phase ?? string.Empty; _detail = detail ?? string.Empty;
            _progress = Math.Max(0f, Math.Min(1f, progress));
            ThrowIfInjectedCancellation();
            if (_active && EditorUtility.DisplayCancelableProgressBar("Avatar Texture Optimizer",
                    _phase + "\n" + _detail, _progress)) ThrowCancelled();
            Refresh.Restart();
        }

        public static void Checkpoint(string detail = null)
        {
            if (!_active) return;
            ThrowIfInjectedCancellation();
            if (Refresh.IsRunning && Refresh.ElapsedMilliseconds < RefreshMilliseconds) return;
            if (!string.IsNullOrEmpty(detail)) _detail = detail;
            if (EditorUtility.DisplayCancelableProgressBar("Avatar Texture Optimizer",
                    _phase + "\n" + _detail, _progress)) ThrowCancelled();
            Refresh.Restart();
        }

        public static void End()
        {
            _active = false; _cancellationProbe = null; Refresh.Reset();
            _phase = _detail = string.Empty; _progress = 0f;
            EditorUtility.ClearProgressBar();
        }

        // Kept for callers compiled against the earlier internal helper.
        public static void Clear() => End();

        private static void ThrowIfInjectedCancellation()
        {
            if (_cancellationProbe != null && _cancellationProbe()) ThrowCancelled();
        }

        private static void ThrowCancelled() =>
            throw new OperationCanceledException("Avatar Texture Optimizer was cancelled by the user.");
    }
}
