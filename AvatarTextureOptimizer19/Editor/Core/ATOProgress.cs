// English: Cancelable progress bar. Cancel stops the bake and releases CPU/GPU/memory, keeping disk temps.
// 中文：可取消进度条。取消会终止烘焙并释放 CPU/GPU/内存，但保留硬盘临时资产。
using System;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal sealed class ATOProgress : IDisposable
    {
        public bool Canceled { get; private set; }
        private bool _shown;

        public void Report(string locKey, float t, string extra = null)
        {
            if (Canceled) return;
            var label = ATOLoc.T(locKey);
            if (!string.IsNullOrEmpty(extra)) label = label + " — " + extra;
            _shown = true;
            if (EditorUtility.DisplayCancelableProgressBar(ATOLoc.T("plugin.displayName"), label, Mathf.Clamp01(t)))
            {
                Canceled = true;
            }
        }

        public void ThrowIfCanceled()
        {
            if (Canceled) throw new ATOCanceledException();
        }

        public void Dispose()
        {
            if (_shown) EditorUtility.ClearProgressBar();
        }
    }

    internal sealed class ATOCanceledException : Exception
    {
        public ATOCanceledException() : base("ATO bake canceled by user")
        {
        }
    }
}
