// BurstRasterizer.cs / BurstRasterizer.cs
// Placeholder for future Burst/Job rasterizer. The CPU path in Rasterization.cs is used for now.
// 未来Burst/Job光栅化的占位。目前使用Rasterization.cs中的CPU路径。

namespace net.fosa.avatar_texture_optimizer.Editor.Atlas
{
    /// <summary>
    /// This class is reserved for a future Burst/Job-accelerated rasterizer.
    /// The current implementation uses the CPU rasterizer in Rasterization.cs.
    /// 这个类预留给未来的Burst/Job加速光栅化器。当前实现在Rasterization.cs中使用CPU光栅化器。
    /// When Unity.Burst and Unity.Collections are available in the project, this can be
    /// upgraded with Burst-compiled jobs for faster rasterization of large meshes.
    /// 当项目中可用Unity.Burst和Unity.Collections时，可将其升级为Burst编译的job，以加速大网格光栅化。
    /// </summary>
    public static class BurstRasterizer
    {
        public const int GRAN = 4;
    }
}
