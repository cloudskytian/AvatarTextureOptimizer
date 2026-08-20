using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// GPU batch hook. Falls back to CPU QualityMetrics when compute is unavailable.
    /// GPU 批量评估入口；无 Compute 时回退 CPU。
    /// </summary>
    public static class GpuQualityBatch
    {
        public static bool Available => SystemInfo.supportsComputeShaders && SystemInfo.supportedRenderTargetCount > 0;

        public static void Warmup()
        {
            AtoLog.Info(Available
                ? "GPU quality path available (RenderTexture). Metrics currently evaluated on CPU fallback for determinism."
                : "GPU compute unavailable; CPU quality metrics only.");
        }
    }
}
