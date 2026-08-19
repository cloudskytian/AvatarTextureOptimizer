// AvatarTextureOptimizer
// File: Editor/Model/TextureUsage.cs
//
// A single texture reference from a material property (or an animation switch).
// Together with the renderer/submesh/UV channel it defines one element of the
// UV->texture mapping the whole tool is built around.
//
// 材质属性（或动画切换）对一张贴图的一次引用。与渲染器/子网格/UV 通道
// 一起构成整个工具所围绕的"UV->贴图"映射中的一个元素。

using System;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.model
{
    /// <summary>
    /// Classification of a texture slot by its usage.
    /// 按用途对贴图槽位的分类。
    /// </summary>
    public enum TextureUsageType
    {
        MainColor,   // 主色贴图（sRGB）/ main color (sRGB)
        NormalMap,   // 法线贴图（切线空间）/ tangent-space normal map
        Mask,        // 蒙版/灰度贴图（单通道多用）/ mask / grayscale
        Unknown,     // 无法判定用途的特殊贴图 / special texture with unknown usage
    }

    /// <summary>
    /// How a material samples a texture — the source of the UV stream and any
    /// ST (scale/offset) transform applied by the material or an animation.
    /// 材质如何采样一张贴图——UV 流的来源以及材质/动画施加的任何 ST 变换。
    /// </summary>
    public sealed class TextureUsage
    {
        /// <summary>The renderer that references this material. / 引用该材质的渲染器。</summary>
        public Renderer Renderer;

        /// <summary>Material slot index on the renderer. / 渲染器上的材质槽索引。</summary>
        public int MaterialSlot;

        /// <summary>The material. / 材质。</summary>
        public Material Material;

        /// <summary>Shader property name (e.g. _MainTex). / 着色器属性名（如 _MainTex）。</summary>
        public string PropertyName;

        /// <summary>The texture. / 贴图。</summary>
        public Texture2D Texture;

        /// <summary>Classification of this slot. / 该槽位的分类。</summary>
        public TextureUsageType Type;

        /// <summary>Which UV channel this property samples (0..7; -1 unknown). / 该属性采样的 UV 通道（0..7；-1 未知）。</summary>
        public int UVChannel = -1;

        /// <summary>ST scale on the material (identity required for optimization). / 材质上的 ST 缩放（必须为单位值才可优化）。</summary>
        public Vector2 STScale = Vector2.one;

        /// <summary>ST offset on the material (zero required). / 材质上的 ST 偏移（必须为 0）。</summary>
        public Vector2 STOffset = Vector2.zero;

        /// <summary>True when the texture is sampled with no transform at all. / 是否完全无变换采样。</summary>
        public bool HasIdentityST => Mathf.Approximately(STScale.x, 1f) && Mathf.Approximately(STScale.y, 1f)
                                     && Mathf.Approximately(STOffset.x, 0f) && Mathf.Approximately(STOffset.y, 0f);

        /// <summary>Whether the texture is imported as sRGB. / 贴图是否以 sRGB 导入。</summary>
        public bool IsSRGB = true;

        /// <summary>Filter mode of the texture. / 贴图的过滤模式。</summary>
        public FilterMode FilterMode = FilterMode.Bilinear;

        /// <summary>The transparent render mode of the referencing material, if known. / 引用材质的透明渲染模式（若已知）。</summary>
        public string RenderMode = "";

        /// <summary>Cutout cutoff threshold of the referencing material, if applicable. / 引用材质的 Cutout 阈值（若适用）。</summary>
        public float Cutoff = 0.5f;

        /// <summary>True when the usage comes from an animation (not the base state). / 是否来自动画（而非基础状态）。</summary>
        public bool FromAnimation = false;

        /// <summary>Human-readable description for logs. / 供日志使用的人类可读描述。</summary>
        public override string ToString()
        {
            string tex = Texture != null ? Texture.name : "<null>";
            string mat = Material != null ? Material.name : "<null>";
            string rnd = Renderer != null ? Renderer.name : "<null>";
            return $"{tex} @ {mat}[{PropertyName}] on {rnd} (slot {MaterialSlot}, uv{UVChannel}{(FromAnimation ? ", anim" : "")})";
        }
    }

    /// <summary>
    /// A unique key identifying one UV space: renderer + material slot + UV
    /// channel. Multi-channel UVs are treated as independent UV spaces.
    /// 标识一个 UV 空间的唯一键：渲染器 + 材质槽 + UV 通道。多通道 UV
    /// 被当作相互独立的 UV 空间处理。
    /// </summary>
    public readonly struct UVSpaceKey : IEquatable<UVSpaceKey>
    {
        public readonly Renderer Renderer;
        public readonly int MaterialSlot;
        public readonly int UVChannel;

        public UVSpaceKey(Renderer renderer, int materialSlot, int uvChannel)
        {
            Renderer = renderer;
            MaterialSlot = materialSlot;
            UVChannel = uvChannel;
        }

        public bool Equals(UVSpaceKey other) =>
            Renderer == other.Renderer && MaterialSlot == other.MaterialSlot && UVChannel == other.UVChannel;

        public override bool Equals(object obj) => obj is UVSpaceKey other && Equals(other);
        public override int GetHashCode() => (Renderer != null ? Renderer.GetHashCode() : 0) * 397 ^ (MaterialSlot * 31) ^ UVChannel;

        public override string ToString() =>
            $"{Renderer?.name ?? "<null>"}#{MaterialSlot}:uv{UVChannel}";
    }
}
