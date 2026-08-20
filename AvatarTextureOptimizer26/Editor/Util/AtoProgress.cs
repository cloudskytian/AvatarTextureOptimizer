using System;
using UnityEditor;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Cancelable progress bar. / 可取消进度条。
    /// </summary>
    public sealed class AtoProgress : IDisposable
    {
        private bool _disposed;
        public int StageCount { get; }
        public int Stage { get; private set; }
        public string StageName { get; private set; } = "";

        public AtoProgress(int stageCount)
        {
            StageCount = Math.Max(1, stageCount);
        }

        public void Set(int stage, string name, float inner = 0f)
        {
            Stage = stage;
            StageName = name ?? "";
            var t = (stage + Math.Clamp(inner, 0f, 0.99f)) / StageCount;
            if (EditorUtility.DisplayCancelableProgressBar("Avatar Texture Optimizer", StageName, t))
                throw new AtoCanceledException();
        }

        public void Inner(float inner)
        {
            Set(Stage, StageName, inner);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EditorUtility.ClearProgressBar();
        }
    }
}
