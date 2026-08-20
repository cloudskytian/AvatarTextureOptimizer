using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Texture usage kinds. / 贴图用途类型。
    /// </summary>
    [Flags]
    public enum AtoTextureKind
    {
        /// <summary>None / 无。</summary>
        None = 0,
        /// <summary>Main color texture. / 主色贴图。</summary>
        Main = 1 << 0,
        /// <summary>Normal map (tangent-space). / 法线贴图（切线空间）。</summary>
        Normal = 1 << 1,
        /// <summary>Mask/grayscale texture (data in specific channels). / 蒙版/灰度贴图（数据在特定通道）。</summary>
        Mask = 1 << 2,
        /// <summary>Tangent/anisotropy data (must never be rotated/re-baked). / 切线/各向异性数据（绝不旋转/重算）。</summary>
        Tangent = 1 << 3,
        /// <summary>Unknown usage — treated as whitelist. / 未知用途——视作白名单。</summary>
        Unknown = 1 << 4,
    }

    /// <summary>
    /// How one material property uses a texture. / 一个材质属性如何使用一张贴图。
    /// </summary>
    public sealed class AtoTextureUsage
    {
        /// <summary>Classification. / 分类。</summary>
        public AtoTextureKind Kind = AtoTextureKind.Unknown;

        /// <summary>Whether the shader marks the property [NoScaleOffset] (ST ignored). / 着色器是否标记 [NoScaleOffset]（忽略 ST）。</summary>
        public bool NoScaleOffset;

        /// <summary>Texture ST scale on the material (must be (1,1) to process). / 材质上的 ST 缩放（须为 (1,1) 才处理）。</summary>
        public Vector2 StScale = Vector2.one;

        /// <summary>Texture ST offset on the material (must be (0,0) to process). / 材质上的 ST 平移（须为 (0,0) 才处理）。</summary>
        public Vector2 StOffset = Vector2.zero;

        /// <summary>UV channel used for sampling (-1 = unknown → whitelist). / 采样所用 UV 通道（-1=未知→白名单）。</summary>
        public int UvChannel = -1;

        /// <summary>Texture must be sampled in sRGB (gamma) space. / 贴图是否以 sRGB 采样。</summary>
        public bool Srgb;

        /// <summary>Which color channels are actually used (R/G/B/A bits). / 实际使用的颜色通道（R/G/B/A 位）。</summary>
        public int UsedChannels = 0b1111;

        /// <summary>Cutout alpha: referenced materials with cutout rendering & their thresholds. / Cutout alpha：引用材质的裁剪阈值集合。</summary>
        public List<(Material material, float cutoff)> CutoutThresholds = new List<(Material, float)>();

        /// <summary>Whether any referencing material uses alpha blend. / 是否有引用材质使用 alpha blend。</summary>
        public bool HasBlend;

        /// <summary>Whether the property is animated (object reference curves swap the texture). / 该属性是否被动画切换贴图。</summary>
        public bool Animated;
    }

    /// <summary>
    /// One (material, property) slot referencing a texture. / 一个引用贴图的（材质, 属性）槽位。
    /// </summary>
    public sealed class AtoTextureSlot
    {
        /// <summary>The material that references the texture. / 引用该贴图的材质。</summary>
        public Material Material;

        /// <summary>Shader property name. / 着色器属性名。</summary>
        public string PropertyName;

        /// <summary>The texture. / 贴图。</summary>
        public Texture2D Texture;

        /// <summary>Usage info. / 使用方式。</summary>
        public AtoTextureUsage Usage = new AtoTextureUsage();

        /// <summary>The renderer material slot this material is (or can be) assigned to. / 该材质所在（或可被动画切到）的渲染器材质槽。</summary>
        public List<(Renderer renderer, int slotIndex)> AssignedSlots = new List<(Renderer, int)>();

        public string FullName => $"{Material.name}.{PropertyName}";
    }

    /// <summary>
    /// Per-texture processing record. / 每张贴图的处理记录。
    /// </summary>
    public sealed class AtoTextureRecord
    {
        /// <summary>The original texture. / 原贴图。</summary>
        public Texture2D Texture;

        /// <summary>All slots referencing this texture. / 引用该贴图的全部槽位。</summary>
        public List<AtoTextureSlot> Slots = new List<AtoTextureSlot>();

        /// <summary>Content + import-settings hash used for deduplication. / 去重用的内容+导入设置哈希。</summary>
        public string DedupeHash;

        /// <summary>Whether whitelisted (skips ALL optimization including import params). / 是否白名单（跳过一切优化含导入参数）。</summary>
        public bool Whitelisted;

        /// <summary>Why it was whitelisted (user whitelist / unsafe usage / etc.). / 白名单原因。</summary>
        public string WhitelistReason;

        /// <summary>The effective texture after processing (atlas member or scaled whole texture). / 处理后的有效贴图（图集成员或缩放后整图）。</summary>
        public Texture2D Result;

        /// <summary>Source bytes (estimated uncompressed size). / 源体积（估算未压缩大小）。</summary>
        public long BytesBefore;

        /// <summary>Result bytes. / 结果体积。</summary>
        public long BytesAfter;

        /// <summary>Whether this texture ended up inside an atlas. / 是否最终进入图集。</summary>
        public bool InAtlas;
    }

    /// <summary>
    /// Subset of import settings relevant for ATO decisions. / 与 ATO 决策相关的导入设置子集。
    /// </summary>
    public sealed class AtoImportSettings
    {
        public bool SrgbTexture;
        public FilterMode FilterMode;
        public TextureWrapMode WrapModeU;
        public TextureWrapMode WrapModeV;
        public int AnisoLevel;
        public bool MipMapEnabled;
        public bool StreamingMipmaps;
        public TextureImporterCompression Compression;
        public bool CrunchCompression;
        public float CrunchCompressionQuality;
        public bool IsReadable;
        public bool AlphaIsTransparency;
        public int MaxTextureSize;
        public TextureImporterNPOTScale NpotScale;
        public TextureImporterFormat PcFormat;
        public TextureImporterFormat AndroidFormat;
        public TextureImporterFormat IosFormat;

        /// <summary>Build the dedupe key from actual pixel content hash + these settings. / 由像素哈希+本设置构建去重键。</summary>
        public string BuildKey(string contentHash)
        {
            // 不同导入设置视作不同贴图（用户要求）。
            return contentHash + "|" +
                   SrgbTexture + "|" + (int)FilterMode + "|" + (int)WrapModeU + "|" + (int)WrapModeV + "|" +
                   AnisoLevel + "|" + MipMapEnabled + "|" + StreamingMipmaps + "|" + (int)Compression + "|" +
                   CrunchCompression + "|" + CrunchCompressionQuality + "|" + IsReadable + "|" +
                   AlphaIsTransparency + "|" + MaxTextureSize + "|" + (int)NpotScale + "|" +
                   (int)PcFormat + "|" + (int)AndroidFormat + "|" + (int)IosFormat;
        }
    }
}
