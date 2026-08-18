// TextureTypeGroup.cs / TextureTypeGroup.cs
// A texture type group is a set of texture layers (e.g. "BaseColor+sRGB+Bilinear+nonormal" vs
// "BaseColor+Normal+sRGB+Bilinear" etc.) that MUST be packed into the same atlas set. Islands
// belonging to the same UV group will be aligned across atlases of different type groups.
// 贴图类型组是一组必须打包进同一图集集合的贴图层（例如"主色+sRGB+双线性+无法线"与"主色+法线+sRGB+双线性"等）。
// 同一UV组的岛在不同类型组的图集上位置必须对齐。

using System.Collections.Generic;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Groups
{
    /// <summary>
    /// Key that uniquely identifies a type group (same colour space + filter mode + same set of auxiliary maps present).
    /// 唯一标识一个类型组的键（相同色彩空间+相同过滤模式+相同辅助贴图集合）。
    /// </summary>
    public struct TextureTypeGroupKey
    {
        public bool sRGB;
        public FilterMode filterMode;
        public TextureUsageFlags usage; // which layers exist across UVs in this group / 此组UV存在哪些层
        public bool hasAlphaChannel;

        public override int GetHashCode()
        {
            unchecked
            {
                int h = sRGB ? 1 : 0;
                h = (h * 397) ^ (int)filterMode;
                h = (h * 397) ^ (int)usage;
                h = (h * 397) ^ (hasAlphaChannel ? 1 : 0);
                return h;
            }
        }

        public bool Equals(TextureTypeGroupKey other) =>
            sRGB == other.sRGB && filterMode == other.filterMode && usage == other.usage && hasAlphaChannel == other.hasAlphaChannel;
        public override bool Equals(object obj) => obj is TextureTypeGroupKey k && Equals(k);
    }

    /// <summary>
    /// A group of texture islands that share the same texture properties and can be packed into shared atlases.
    /// 一组共享相同贴图属性的贴图岛，可以打包到共享图集里。
    /// </summary>
    public class TextureTypeGroup
    {
        public TextureTypeGroupKey Key;
        /// <summary>UV groups in this type group (each contributes one island per texture layer present) / 本类型组中的UV组（每个为存在的贴图层贡献一个岛）</summary>
        public List<UVGroup> UvGroups = new();
        /// <summary>Atlases generated for this type group / 为本类型组生成的图集</summary>
        public List<Atlas.AtlasTexture> Atlases = new();
        /// <summary>Whether this group requires alpha in the atlas / 此组是否需要图集有Alpha通道</summary>
        public bool NeedsAlpha;
        /// <summary>Whether this group is for normal maps / 此组是否为法线贴图</summary>
        public bool IsNormal => (Key.usage & TextureUsageFlags.Normal) != 0;
        /// <summary>Whether this group is grayscale / 此组是否为灰度</summary>
        public bool IsGrayscale => (Key.usage & TextureUsageFlags.Grayscale) != 0;
    }
}
