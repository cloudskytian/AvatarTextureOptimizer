using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Type group key: texture kinds signature × color space × filter mode. / 类型组键：贴图类型签名 × 色彩空间 × filterMode。
    /// Textures whose special-texture composition differs (e.g. one has a normal map and another does not)
    /// must live in different groups, so that atlases do not waste space. / 特殊贴图构成不同的贴图
    /// （例如一个有对应法线贴图一个没有）必须在不同组，避免图集空间浪费。
    /// </summary>
    public readonly struct AtoTypeGroupKey : IEquatable<AtoTypeGroupKey>
    {
        /// <summary>Sortable signature of the texture kinds used together (e.g. "Main|Normal|Mask"). / 同用贴图类型的排序签名（如 "Main|Normal|Mask"）。</summary>
        public readonly string KindSignature;

        /// <summary>sRGB color space. / sRGB 色彩空间。</summary>
        public readonly bool Srgb;

        /// <summary>Filter mode. / 过滤模式。</summary>
        public readonly FilterMode Filter;

        public AtoTypeGroupKey(string kindSignature, bool srgb, FilterMode filter)
        {
            KindSignature = kindSignature;
            Srgb = srgb;
            Filter = filter;
        }

        public bool Equals(AtoTypeGroupKey other) =>
            KindSignature == other.KindSignature && Srgb == other.Srgb && Filter == other.Filter;

        public override bool Equals(object obj) => obj is AtoTypeGroupKey other && Equals(other);

        public override int GetHashCode() =>
            (KindSignature?.GetHashCode() ?? 0) ^ (Srgb ? 1 : 0) ^ ((int)Filter << 1);
    }

    /// <summary>
    /// A texture type group: all texture slots with the same kind signature/color space/filter mode. /
    /// 一个贴图类型组：类型签名/色彩空间/filterMode 相同的全部贴图槽。
    /// Generates zero or more atlases (atlas count is not limited). / 生成 0..n 张图集（数量不限）。
    /// </summary>
    public sealed class AtoTypeGroup
    {
        public AtoTypeGroupKey Key;

        /// <summary>All slots in this group. / 组内全部槽位。</summary>
        public List<AtoTextureSlot> Slots = new List<AtoTextureSlot>();

        /// <summary>All UV groups participating. / 参与的全部 UV 组。</summary>
        public HashSet<AtoUvGroup> UvGroups = new HashSet<AtoUvGroup>();

        /// <summary>All atlases produced for this group. / 该组生成的全部图集。</summary>
        public List<AtoAtlas> Atlases = new List<AtoAtlas>();

        /// <summary>Contains tangent/anisotropy data → rotation is disabled everywhere in this group. / 含切线/各向异性数据 → 本组全面禁用旋转。</summary>
        public bool ContainsTangentData;

        /// <summary>Whether any slot in this group requires alpha (transparent content). / 组内是否有槽需要 alpha（透明内容）。</summary>
        public bool HasAlpha;

        public string DisplayName => Key.KindSignature + (Key.Srgb ? "|sRGB" : "|Linear") + "|" + Key.Filter;

        public AtoTypeGroup(AtoTypeGroupKey key)
        {
            Key = key;
        }
    }

    /// <summary>
    /// One atlas: produced by packing one or more textures of a type group. / 一张图集：由一个类型组的一张或多张贴图装箱生成。
    /// Every island in the atlas keeps its SHARED UV rect; the atlas only chooses its pixel resolution. /
    /// 图集中的每个岛保持其共享 UV 矩形；图集只决定其像素分辨率。
    /// </summary>
    public sealed class AtoAtlas
    {
        /// <summary>Owning type group. / 所属类型组。</summary>
        public AtoTypeGroup Group;

        /// <summary>Atlas name (starts with ATO_). / 图集名（以 ATO_ 开头）。</summary>
        public string Name;

        public int Width;
        public int Height;

        /// <summary>Islands placed in this atlas (with their shared UV rects). / 放置于本图集的岛（含共享 UV 矩形）。</summary>
        public List<AtoPlacedIsland> Placed = new List<AtoPlacedIsland>();

        /// <summary>Textures whose content feeds this atlas. / 内容来源贴图。</summary>
        public HashSet<Texture2D> SourceTextures = new HashSet<Texture2D>();

        /// <summary>Per placed island: the texture whose content feeds the atlas rect. / 每个放置岛：内容来源贴图。</summary>
        public Dictionary<AtoIsland, Texture2D> SourceByIsland = new Dictionary<AtoIsland, Texture2D>();

        /// <summary>The produced texture asset. / 产出的贴图资产。</summary>
        public Texture2D Result;

        /// <summary>Packed area utilization 0..1. / 打包面积利用率 0..1。</summary>
        public float Utilization;
    }
}
