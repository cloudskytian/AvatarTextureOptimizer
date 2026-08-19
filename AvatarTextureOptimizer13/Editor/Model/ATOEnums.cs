// ATO — Avatar Texture Optimizer
// Core enums shared across the analysis / optimization / packing stages.
// 分析 / 优化 / 装箱各阶段共用的核心枚举。

namespace net.fosa.ato.editor
{
    /// <summary>
    /// The role a texture plays on a material. Determines which metrics apply and how the
    /// texture participates in type groups and atlas kinds.
    /// 贴图在材质上扮演的角色。决定适用哪些质量指标、如何参与类型组与图集类别。
    /// </summary>
    public enum ATOTextureKind
    {
        /// <summary>Main color (albedo). 主色（反照率）。</summary>
        Color = 0,
        /// <summary>Normal map (tangent-space). 法线贴图（切线空间）。</summary>
        NormalMap = 1,
        /// <summary>Mask / grayscale utility (metallic-smoothness, occlusion, emission masks...). 蒙版 / 灰度工具贴图。</summary>
        Mask = 2,
        /// <summary>Grayscale single-purpose texture. 灰度专用贴图。</summary>
        Grayscale = 3,
        /// <summary>Emission color map. 自发光颜色贴图。</summary>
        Emission = 4,
        /// <summary>Other / unrecognized usage (treated with the color metrics as a safe fallback). 其他/未识别用途（按主色指标安全兜底）。</summary>
        Other = 5,
    }

    /// <summary>
    /// Alpha handling mode of a material. Drives alpha quality metrics (IoU vs RMSE).
    /// 材质的透明处理模式，决定 alpha 质量指标（IoU vs RMSE）。
    /// </summary>
    public enum ATOAlphaMode
    {
        /// <summary>Opaque — no alpha metric. 不透明——无 alpha 指标。</summary>
        Opaque = 0,
        /// <summary>Cutout — clipped outline IoU. Cutout——裁剪轮廓 IoU。</summary>
        Cutout = 1,
        /// <summary>Blend (transparent) — linear alpha RMSE. Blend（透明）——线性 alpha RMSE。</summary>
        Blend = 2,
    }
}
