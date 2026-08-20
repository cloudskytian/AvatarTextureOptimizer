// Progress UI with cancellation (editor progress bar). / 进度显示与取消（编辑器进度条）。
// On cancel we throw AtoCancelledException which the pass converts into a clean abort:
// temp assets stay on disk, GPU/CPU/memory are released (finally blocks). / 取消时抛出
// 异常干净中止：临时资产保留，资源在 finally 释放。

using System;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal class AtoCancelledException : OperationCanceledException
    {
        internal AtoCancelledException() : base("ATO bake cancelled by user") { }
    }

    internal static class Progress
    {
        private static string _stage = "ATO";
        private static float _base, _range = 1f;
        private static double _lastRefresh;

        /// <summary>Begin a stage occupying [base, base+range] of the overall bar.
        /// 开始一个占总进度 [base, base+range] 的阶段。</summary>
        internal static void Stage(string name, float base01, float range01)
        {
            _stage = name;
            _base = base01;
            _range = range01;
            ATOLog.DebugL($"stage: {name} [{base01:F2}..{base01 + range01:F2}]");
        }

        /// <summary>Report progress within a stage; pumps cancellation.
        /// 上报某阶段进度；处理取消。</summary>
        internal static void Report(string stage, float t, string info = null)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastRefresh < 0.2 && t < 1f) return; // throttle / 节流
            _lastRefresh = now;

            float overall = Mathf.Clamp01(_base + Mathf.Clamp01(t) * _range);
            string label = string.IsNullOrEmpty(stage) ? _stage : stage;
            if (EditorUtility.DisplayCancelableProgressBar("ATO Avatar Texture Optimizer",
                    $"{label}{(info == null ? "" : " — " + info)}", overall))
                throw new AtoCancelledException();
        }

        internal static void Clear()
        {
            EditorUtility.ClearProgressBar();
        }
    }
}
