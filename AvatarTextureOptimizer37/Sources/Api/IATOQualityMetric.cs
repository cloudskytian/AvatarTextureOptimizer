// ============================================================================
// ATO public API - custom quality metrics
// ATO 公开 API - 自定义质量指标
//
// The built-in metric set (MS-SSIM/SSIM + ΔE2000 + alpha + normal angle +
// gray RMSE) can be complemented by custom metrics. A scaled island PASSES
// only when ALL built-in AND custom metrics pass.
// 内置指标集（MS-SSIM/SSIM + ΔE2000 + alpha + 法线角度 + 灰度 RMSE）可由自定义
// 指标补充。缩放后的岛只有当内置与自定义指标全部达标才算通过。
// ============================================================================

#region

using System;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Api
{
    /// <summary>Read-only pixel access to the two images compared by a metric.
    /// Coordinates are in the ORIGINAL image space; the scaled image is
    /// bilinearly upsampled back to original size before comparison.
    /// 只读像素访问。坐标为原图空间；比较前缩放图已双线性上采样回原尺寸。</summary>
    public interface IATOPixelPlane
    {
        int Width { get; }
        int Height { get; }

        /// <summary>Read one RGBA pixel (linear space, 0..1).
        /// 读取一个 RGBA 像素（线性空间 0~1）。</summary>
        void GetPixel(int x, int y, out float r, out float g, out float b, out float a);
    }

    /// <summary>Context passed to custom metrics.
    /// 传给自定义指标的上下文。</summary>
    public sealed class ATOQualityMetricContext
    {
        /// <summary>Texture category of the island. 岛的贴图类别。</summary>
        public ATOTextureCategory Category;
        /// <summary>0=opaque 1=cutout 2=blend 3=premultiply (strictest across
        /// all referencing materials). 最严格的引用材质透明模式。</summary>
        public int AlphaMode;
        /// <summary>Alpha cutoff of the strictest referencing material.
        /// 最严格引用材质的 alpha 裁剪阈值。</summary>
        public float Cutoff;
        /// <summary>Original (pre-scaling) island content. 原图内容。</summary>
        public IATOPixelPlane Original;
        /// <summary>Scaled content upsampled back to original size.
        /// 缩放后上采样回原尺寸的内容。</summary>
        public IATOPixelPlane Scaled;
        /// <summary>True when the comparison area is the island's actual
        /// covered pixels (not its bounding box).
        /// 比较区域是否为岛实际覆盖像素（而非包围盒）。</summary>
        public bool CoveredPixelsOnly;
        /// <summary>Per-pixel mask (1 = covered). Valid when
        /// <see cref="CoveredPixelsOnly"/> is true. 覆盖掩码。</summary>
        public Func<int, int, bool> Coverage;
    }

    /// <summary>A custom quality metric.
    /// 自定义质量指标。</summary>
    public interface IATOQualityMetric
    {
        /// <summary>Stable unique name (used in logs). 稳定唯一名称（日志用）。</summary>
        string Name { get; }

        /// <summary>Metrics run only for the categories they support.
        /// 指标仅对其支持的类别运行。</summary>
        bool Supports(ATOTextureCategory category, int alphaMode);

        /// <summary>Passes when the scaled content still meets the metric.
        /// 缩放内容仍满足指标时返回 true。</summary>
        bool Evaluate(ATOQualityMetricContext context);
    }
}
