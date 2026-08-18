// Copyright (c) fosa. Licensed under the MIT License.
// Serializable settings, including per-platform overrides.
// 可序列化设置，包含按平台的覆盖配置。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Per-texture-category output settings (compression + mip behaviour).
    /// 按贴图分类的输出设置（压缩 + mip 行为）。
    /// </summary>
    [Serializable]
    public sealed class CategoryOutputSettings
    {
        /// <summary>Compression format for this category. / 该分类使用的压缩格式。</summary>
        public ATOCompressionFormat format = ATOCompressionFormat.Auto;

        /// <summary>
        /// Mipmaps + streaming mip maps. VRChat requires streaming when mipmaps are enabled,
        /// so the two are deliberately bound to a single toggle.
        /// Mipmap 与 MipStreaming。VRChat 要求开启 Mipmap 时必须开启 MipStreaming，故二者绑定为单一开关。
        /// </summary>
        public bool mipmapAndStreaming = true;

        /// <summary>Compression quality 0-100 where supported. / 支持时的压缩质量 0-100。</summary>
        [Range(0, 100)]
        public int compressionQuality = 50;

        /// <summary>Creates an independent copy. / 创建独立副本。</summary>
        public CategoryOutputSettings Clone() => (CategoryOutputSettings)MemberwiseClone();
    }

    /// <summary>
    /// The full set of tunables that can be overridden per platform.
    /// 可按平台覆盖的完整可调参数集合。
    /// </summary>
    [Serializable]
    public sealed class PlatformSettings
    {
        /// <summary>Which platform this block configures. / 该配置块对应的平台。</summary>
        public ATOPlatform platform = ATOPlatform.PC;

        /// <summary>
        /// When false the block is ignored and the shared defaults are used.
        /// 为 false 时忽略该块，使用通用默认值。
        /// </summary>
        public bool enabled;

        /// <summary>Quality tier for this platform. / 该平台的质量挡位。</summary>
        public QualityTier tier = QualityTier.Balanced;

        /// <summary>Resolved thresholds for the selected tier. / 所选挡位解析出的阈值。</summary>
        public QualityParameters quality = QualityPresets.Create(QualityTier.Balanced);

        /// <summary>Persistent custom tier values, never overwritten by tier switching. / 持久化的自定义挡位值，切换挡位不会覆盖。</summary>
        public QualityParameters customQuality = QualityPresets.Create(QualityTier.Custom);

        /// <summary>Generate atlases. When off, textures are only rescaled as a whole. / 是否生成图集。关闭时仅整体缩放贴图。</summary>
        public bool generateAtlas = true;

        /// <summary>Minimum padding between islands. / 岛间最小间距。</summary>
        public AtlasPadding minPadding = AtlasPadding.Px4;

        /// <summary>Experimental non-power-of-two atlas sizes. / 实验性 NPOT 图集分辨率。</summary>
        public bool allowNpot;

        /// <summary>Deduplicate materials with identical content. / 对内容相同的材质去重。</summary>
        public bool deduplicateMaterials = true;

        /// <summary>Deduplicate textures and atlases with identical content. / 对内容相同的贴图与图集去重。</summary>
        public bool deduplicateTextures = true;

        /// <summary>Output settings for opaque colour textures. / 不透明颜色贴图的输出设置。</summary>
        public CategoryOutputSettings opaqueColor = new CategoryOutputSettings();

        /// <summary>Output settings for textures with alpha. / 带 alpha 的贴图输出设置。</summary>
        public CategoryOutputSettings transparentColor = new CategoryOutputSettings();

        /// <summary>Output settings for normal maps. / 法线贴图的输出设置。</summary>
        public CategoryOutputSettings normalMap = new CategoryOutputSettings();

        /// <summary>Output settings for grayscale/mask textures. / 灰度/蒙版贴图的输出设置。</summary>
        public CategoryOutputSettings grayscale = new CategoryOutputSettings();

        /// <summary>
        /// Maximum atlas side length. Clamped to 4096 on mobile platforms at build time.
        /// 图集最大边长。构建时在移动平台钳制到 4096。
        /// </summary>
        public int maxAtlasSize = 8192;

        /// <summary>Returns the active thresholds, honouring the Custom tier. / 返回生效的阈值，正确处理 Custom 挡位。</summary>
        public QualityParameters ResolveQuality()
        {
            return tier == QualityTier.Custom ? customQuality : quality;
        }

        /// <summary>
        /// Re-derives <see cref="quality"/> from <see cref="tier"/>. The custom tier is left
        /// untouched so user edits survive tier switching.
        /// 依据 <see cref="tier"/> 重新推导 <see cref="quality"/>。Custom 挡位保持不变，使用户修改在切换挡位后依然保留。
        /// </summary>
        public void ApplyTierDefaults()
        {
            if (tier == QualityTier.Custom) return;
            quality = QualityPresets.Create(tier);
        }

        /// <summary>Creates a deep copy. / 创建深拷贝。</summary>
        public PlatformSettings Clone()
        {
            var c = (PlatformSettings)MemberwiseClone();
            c.quality = quality?.Clone();
            c.customQuality = customQuality?.Clone();
            c.opaqueColor = opaqueColor?.Clone();
            c.transparentColor = transparentColor?.Clone();
            c.normalMap = normalMap?.Clone();
            c.grayscale = grayscale?.Clone();
            return c;
        }

        /// <summary>Returns the settings for one texture category. / 返回某个贴图分类的设置。</summary>
        public CategoryOutputSettings GetCategory(TextureCategory category)
        {
            switch (category)
            {
                case TextureCategory.OpaqueColor: return opaqueColor;
                case TextureCategory.TransparentColor: return transparentColor;
                case TextureCategory.NormalMap: return normalMap;
                case TextureCategory.Grayscale: return grayscale;
                default: return opaqueColor;
            }
        }
    }

    /// <summary>
    /// Root settings object stored on the avatar component.
    /// 存储在 Avatar 组件上的根设置对象。
    /// </summary>
    [Serializable]
    public sealed class ATOSettings
    {
        /// <summary>Shared defaults, used when no platform override applies. / 通用默认值，无平台覆盖时使用。</summary>
        public PlatformSettings shared = new PlatformSettings();

        /// <summary>Per-platform overrides. / 按平台的覆盖配置。</summary>
        public List<PlatformSettings> platformOverrides = new List<PlatformSettings>
        {
            new PlatformSettings { platform = ATOPlatform.PC, enabled = false },
            new PlatformSettings { platform = ATOPlatform.Android, enabled = false },
            new PlatformSettings { platform = ATOPlatform.iOS, enabled = false },
        };

        /// <summary>
        /// Objects whose referenced textures are excluded from all optimization.
        /// Accepts any object type: renderers, materials, textures, animation clips, GameObjects.
        /// 白名单对象，其引用的贴图跳过所有优化。接受任意类型：渲染器、材质、贴图、动画、游戏对象。
        /// </summary>
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        /// <summary>Emit verbose per-step logs to the Unity console. / 向 Unity 控制台输出详细的分步日志。</summary>
        public bool verboseLogging;

        /// <summary>UI language mode. / 界面语言模式。</summary>
        public LanguageMode languageMode = LanguageMode.Auto;

        /// <summary>Explicit language code when <see cref="languageMode"/> is Explicit. / languageMode 为 Explicit 时使用的语言代码。</summary>
        public string explicitLanguage = "en";

        /// <summary>
        /// Resolves the effective settings for a platform, falling back to the shared block.
        /// 解析某平台的生效设置，未启用覆盖时回退到通用配置。
        /// </summary>
        public PlatformSettings Resolve(ATOPlatform platform)
        {
            if (platformOverrides != null)
            {
                foreach (var p in platformOverrides)
                {
                    if (p != null && p.enabled && p.platform == platform) return p;
                }
            }

            return shared;
        }

        /// <summary>Returns the override block for a platform, creating it if missing. / 返回某平台的覆盖块，不存在时创建。</summary>
        public PlatformSettings GetOrCreateOverride(ATOPlatform platform)
        {
            platformOverrides ??= new List<PlatformSettings>();
            foreach (var p in platformOverrides)
            {
                if (p != null && p.platform == platform) return p;
            }

            var created = new PlatformSettings { platform = platform, enabled = false };
            platformOverrides.Add(created);
            return created;
        }
    }
}
