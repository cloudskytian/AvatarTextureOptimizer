// Progress display & cancellation. / 进度显示与取消支持。
using System;
using UnityEditor;

namespace net.fosa.ato.editor
{
    /// <summary>Thrown when the user cancels baking. / 用户取消烘焙时抛出。</summary>
    public class AtoCancelledException : OperationCanceledException
    {
        public AtoCancelledException() : base("[ATO] Bake cancelled by user.") { }
    }

    /// <summary>
    /// Cancelable progress reporting. Cancel keeps temp assets on disk but releases CPU/GPU/RAM
    /// via the processor's finally-cleanup. / 可取消进度条；取消时保留临时资产但释放资源。
    /// </summary>
    public static class AtoProgress
    {
        public static bool Enabled = true;
        private static string _stage = "";

        public static void BeginStage(string stageName)
        {
            _stage = stageName;
            Step(0f, "");
        }

        /// <summary>Report progress; throws AtoCancelledException on user cancel. / 上报进度，取消时抛异常。</summary>
        public static void Step(float progress01, string detail)
        {
            if (!Enabled) return;
            bool cancel = EditorUtility.DisplayCancelableProgressBar(
                "Avatar Texture Optimizer",
                string.IsNullOrEmpty(detail) ? _stage : _stage + " - " + detail,
                Math.Clamp(progress01, 0f, 1f));
            if (cancel) throw new AtoCancelledException();
        }

        public static void Clear()
        {
            if (Enabled) EditorUtility.ClearProgressBar();
        }
    }
}
