// Avatar Texture Optimizer / 头像贴图优化器
// Texture role vocabulary shared by the analyzer.
// 贴图角色词汇表，供分析器使用。

using System;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Role of a texture slot. Determines which quality metrics apply and which
    /// texture-type-group the slot joins for atlas generation.
    /// 贴图槽的角色。决定使用哪套质量指标以及生成图集时归入哪个贴图类型组。
    /// </summary>
    public enum ATORole
    {
        /// <summary>Unknown / unsupported slot (treated as whitelist). / 未知/不支持（按白名单处理）。</summary>
        Unknown = 0,
        /// <summary>Main color (sRGB, possibly alpha) / 主色（sRGB，可能带 alpha）。</summary>
        Main = 1,
        /// <summary>Additional main layers (lilToon 2nd/3rd main) / 附加主色层（lilToon 第2/3层主色）。</summary>
        MainLayer = 2,
        /// <summary>Normal map / 法线贴图。</summary>
        Normal = 3,
        /// <summary>Grayscale mask (metallic/smoothness/AO/blend masks...) / 灰度蒙版（金属度/光滑度/AO/混合蒙版…）。</summary>
        Mask = 4,
        /// <summary>Emission color / 自发光颜色。</summary>
        Emission = 5,
    }

    /// <summary>
    /// Why a texture is excluded from optimization (acts as whitelist).
    /// 贴图被排除优化（按白名单处理）的原因。
    /// </summary>
    [Flags]
    public enum ATOExcludeReason
    {
        None = 0,
        /// <summary>User whitelist / 用户白名单。</summary>
        UserWhitelist = 1 << 0,
        /// <summary>Non-identity UV ST (static or animated) / UV ST 非恒等（静态或动画）。</summary>
        UvTransform = 1 << 1,
        /// <summary>Special-purpose texture (matcap/ramp/parallax/decal/fur/audiolink/screen-space...) / 特殊用途贴图。</summary>
        SpecialPurpose = 1 << 2,
        /// <summary>Unknown shader/property configuration / 未识别的着色器或属性配置。</summary>
        UnknownShader = 1 << 3,
        /// <summary>UV out-of-range, repeat wrap, or seam crossing / UV 越界、repeat 接缝跨越或无法归一。</summary>
        UvOutOfRange = 1 << 4,
        /// <summary>Duplicate adopted a whitelisted source / 去重合并源中含白名单。</summary>
        DedupTainted = 1 << 5,
        /// <summary>Not a readable Texture2D or unsupported texture type / 不可读或非 Texture2D。</summary>
        NotTexture2D = 1 << 6,
        /// <summary>Animated to a state we cannot prove safe / 动画导致无法证明安全。</summary>
        AnimatedUnsafe = 1 << 7,
        /// <summary>Renderer is stripped or EditorOnly / 渲染器被移除或 EditorOnly。</summary>
        RendererSkipped = 1 << 8,
        /// <summary>Platform format constraint fallback / 平台格式约束兜底。</summary>
        PlatformFallback = 1 << 9,
        /// <summary>Used by a whitelisted object graph / 被白名单对象图引用。</summary>
        WhitelistedGraph = 1 << 10,
        /// <summary>UV channel used by AAO with no free evacuation channel / AAO 占用 UV 通道且无空闲转移目标。</summary>
        AaoUvBlocked = 1 << 11,
        /// <summary>Non-direct mesh UV sampling (unknown UV source) / 非直接网格 UV 采样。</summary>
        NonMeshUv = 1 << 12,
        /// <summary>Channel usage unknown; conservative fallback / 通道用途未知的保守兜底。</summary>
        UnknownChannelUsage = 1 << 13,
    }

    /// <summary>
    /// A analyzed view of one texture property on one material.
    /// 某材质上某个贴图属性的一份分析视图。
    /// </summary>
    public sealed class ATOTextureSlot
    {
        /// <summary>Property name / 属性名。</summary>
        public string propertyName;
        /// <summary>Assigned texture (may be null) / 已赋值的贴图（可空）。</summary>
        public UnityEngine.Texture texture;
        /// <summary>Role / 角色。</summary>
        public ATORole role = ATORole.Unknown;
        /// <summary>Which UV channel feeds the sampler (0..7) / 采样器使用的 UV 通道（0..7）。</summary>
        public int uvChannel;
        /// <summary>Channels actually consumed by the shader (RGBA bitmask) / 着色器实际消费的通道（RGBA 位掩码）。</summary>
        public int usedChannelsMask = 0xF;
        /// <summary>Exclusion flags; None means freely optimizable / 排除标记；None 表示可自由优化。</summary>
        public ATOExcludeReason exclusion = ATOExcludeReason.None;
        /// <summary>Optional human-readable note / 可读备注。</summary>
        public string note;
    }
}
