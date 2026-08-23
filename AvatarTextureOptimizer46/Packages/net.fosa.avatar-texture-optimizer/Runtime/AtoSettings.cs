// SPDX-License-Identifier: MIT
// EN: Serializable settings model stored on the avatar component.
// ZH: 存放在 Avatar 组件上的可序列化设置模型。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// EN: Texture import/output options for one texture kind on one platform.
    /// ZH: 单个平台下、单个贴图分类的导入/输出选项。
    /// </summary>
    [Serializable]
    public sealed class AtoTextureKindSettings
    {
        /// <summary>EN: Generate mipmaps. VRChat requires mip streaming when mipmaps exist, so the two are bound together. ZH: 生成 Mipmap。VRChat 要求有 Mipmap 时必须开启 MipStreaming，因此二者绑定。</summary>
        public bool mipmapAndStreaming = true;

        /// <summary>EN: Compression format for opaque colour atlases. ZH: 不透明颜色图集的压缩格式。</summary>
        public AtoColorOpaqueFormat colorOpaqueFormat = AtoColorOpaqueFormat.Auto;

        /// <summary>EN: Compression format for colour atlases with alpha. ZH: 带 alpha 的颜色图集的压缩格式。</summary>
        public AtoColorAlphaFormat colorAlphaFormat = AtoColorAlphaFormat.Auto;

        /// <summary>EN: Compression format for normal atlases. ZH: 法线图集的压缩格式。</summary>
        public AtoNormalFormat normalFormat = AtoNormalFormat.Auto;

        /// <summary>EN: Compression format for grayscale/mask atlases. ZH: 灰度/蒙版图集的压缩格式。</summary>
        public AtoGrayscaleFormat grayscaleFormat = AtoGrayscaleFormat.Auto;

        /// <summary>EN: Deep copy. ZH: 深拷贝。</summary>
        public AtoTextureKindSettings Clone() => (AtoTextureKindSettings)MemberwiseClone();
    }

    /// <summary>
    /// EN: A complete set of optimization parameters. One instance is the "common" profile, and each
    ///     enabled platform override owns another instance.
    /// ZH: 一整套优化参数。通用配置为一个实例，每个启用的平台覆盖各自再持有一个实例。
    /// </summary>
    [Serializable]
    public sealed class AtoProfile
    {
        /// <summary>EN: Selected quality tier. ZH: 选中的质量挡位。</summary>
        public AtoQualityTier tier = AtoQualityTier.Balanced;

        /// <summary>EN: Parameters of the currently selected non-custom tier. ZH: 当前选中的非自定义挡位的参数。</summary>
        public AtoQualityParameters quality = AtoQualityPresets.Create(AtoQualityTier.Balanced);

        /// <summary>EN: Parameters of the custom tier. Never overwritten by tier switches. ZH: 自定义挡位的参数，切换挡位时永不被覆盖。</summary>
        public AtoQualityParameters customQuality = AtoQualityPresets.Create(AtoQualityTier.Custom);

        /// <summary>EN: Generate atlases. When off, whole textures are scaled instead and UVs are untouched. ZH: 生成图集。关闭时改为缩放整张贴图且不修改 UV。</summary>
        public bool generateAtlas = true;

        /// <summary>EN: Allow non power of two atlas sizes (64 px steps). Experimental. ZH: 允许非二次幂图集尺寸（64 像素步进）。实验性。</summary>
        public bool allowNpot = false;

        /// <summary>EN: Minimum padding between islands. ZH: 岛之间的最小间距。</summary>
        public AtoPaddingOption minPadding = AtoPaddingOption.Px4;

        /// <summary>EN: Per texture kind output settings. ZH: 按贴图分类的输出设置。</summary>
        public AtoTextureKindSettings textures = new AtoTextureKindSettings();

        /// <summary>EN: Deduplicate identical materials after optimization. ZH: 优化后对完全相同的材质去重。</summary>
        public bool dedupeMaterials = true;

        /// <summary>EN: Deduplicate identical textures/atlases after optimization. ZH: 优化后对完全相同的贴图/图集去重。</summary>
        public bool dedupeTextures = true;

        /// <summary>
        /// EN: Returns the parameter set that should actually be used (custom tier honoured).
        /// ZH: 返回真正应当使用的参数集（正确处理自定义挡位）。
        /// </summary>
        public AtoQualityParameters EffectiveQuality => tier == AtoQualityTier.Custom ? customQuality : quality;

        /// <summary>EN: Deep copy. ZH: 深拷贝。</summary>
        public AtoProfile Clone()
        {
            var c = (AtoProfile)MemberwiseClone();
            c.quality = quality.Clone();
            c.customQuality = customQuality.Clone();
            c.textures = textures.Clone();
            return c;
        }
    }

    /// <summary>
    /// EN: One platform override entry, mirroring Unity's texture platform override UI.
    /// ZH: 一条平台覆盖项，对应 Unity 的贴图 platform override 界面。
    /// </summary>
    [Serializable]
    public sealed class AtoPlatformOverride
    {
        /// <summary>EN: Which platform this override targets. ZH: 该覆盖针对的平台。</summary>
        public AtoPlatform platform = AtoPlatform.PC;

        /// <summary>EN: Whether the override is active. ZH: 该覆盖是否启用。</summary>
        public bool enabled = false;

        /// <summary>EN: The overriding parameters. ZH: 覆盖用的参数。</summary>
        public AtoProfile profile = new AtoProfile();
    }

    /// <summary>
    /// EN: Root settings object for the whole optimizer.
    /// ZH: 整个优化器的根设置对象。
    /// </summary>
    [Serializable]
    public sealed class AtoSettings
    {
        /// <summary>EN: Parameters used when no platform override applies. ZH: 无平台覆盖时使用的参数。</summary>
        public AtoProfile common = new AtoProfile();

        /// <summary>EN: Platform specific overrides. ZH: 平台特定覆盖。</summary>
        public List<AtoPlatformOverride> platformOverrides = new List<AtoPlatformOverride>
        {
            new AtoPlatformOverride { platform = AtoPlatform.PC },
            new AtoPlatformOverride { platform = AtoPlatform.Android },
            new AtoPlatformOverride { platform = AtoPlatform.iOS },
        };

        /// <summary>
        /// EN: Objects excluded from all optimization. Any object type is accepted: GameObject, Renderer,
        ///     Mesh, Material, Texture, AnimationClip, AnimatorController and so on. Every texture that is
        ///     reachable from a whitelisted object is left untouched.
        /// ZH: 排除在所有优化之外的对象。接受任意类型：GameObject、Renderer、Mesh、Material、Texture、
        ///     AnimationClip、AnimatorController 等。凡是能从白名单对象到达的贴图都保持原样。
        /// </summary>
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        /// <summary>EN: Emit verbose [ATO] logs to the Unity console. ZH: 向 Unity 控制台输出详细的 [ATO] 日志。</summary>
        public bool verboseLogging = false;

        /// <summary>EN: Emit per-island trace logs. Extremely noisy; for tool development only. ZH: 输出逐岛跟踪日志。极其冗长，仅供工具开发使用。</summary>
        public bool traceLogging = false;

        /// <summary>EN: Language code for the inspector, or "auto" to follow NDMF. ZH: 检视面板语言代码，"auto" 表示跟随 NDMF。</summary>
        public string language = "auto";

        /// <summary>
        /// EN: Resolves the profile to use for a build platform, applying the override when enabled.
        /// ZH: 解析构建平台应使用的配置，启用时应用对应覆盖。
        /// </summary>
        public AtoProfile Resolve(AtoPlatform platform)
        {
            if (platformOverrides != null)
            {
                foreach (var o in platformOverrides)
                {
                    if (o != null && o.enabled && o.platform == platform && o.profile != null)
                        return o.profile;
                }
            }
            return common;
        }
    }
}
