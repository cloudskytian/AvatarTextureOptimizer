using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Dedup key: pixels + importer settings. Different importer => different key.
    /// 去重键：像素 + 导入设置，导入设置不同即视为不同贴图。
    /// </summary>
    public readonly struct TextureKey : IEquatable<TextureKey>
    {
        public readonly Texture2D Texture;
        public readonly int Width;
        public readonly int Height;
        public readonly TextureFormat Format;
        public readonly bool Srgb;
        public readonly FilterMode Filter;
        public readonly TextureWrapMode WrapU;
        public readonly TextureWrapMode WrapV;
        public readonly int Aniso;
        public readonly int ContentHash;

        public TextureKey(Texture2D tex, int contentHash, bool srgb)
        {
            Texture = tex;
            Width = tex != null ? tex.width : 0;
            Height = tex != null ? tex.height : 0;
            Format = tex != null ? tex.format : TextureFormat.RGBA32;
            Srgb = srgb;
            Filter = tex != null ? tex.filterMode : FilterMode.Bilinear;
            WrapU = tex != null ? tex.wrapModeU : TextureWrapMode.Repeat;
            WrapV = tex != null ? tex.wrapModeV : TextureWrapMode.Repeat;
            Aniso = tex != null ? tex.anisoLevel : 1;
            ContentHash = contentHash;
        }

        public bool Equals(TextureKey other)
        {
            return Width == other.Width && Height == other.Height && Format == other.Format &&
                   Srgb == other.Srgb && Filter == other.Filter && WrapU == other.WrapU &&
                   WrapV == other.WrapV && Aniso == other.Aniso && ContentHash == other.ContentHash;
        }

        public override bool Equals(object obj) => obj is TextureKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Width;
                h = (h * 397) ^ Height;
                h = (h * 397) ^ (int)Format;
                h = (h * 397) ^ (Srgb ? 1 : 0);
                h = (h * 397) ^ (int)Filter;
                h = (h * 397) ^ ContentHash;
                return h;
            }
        }
    }
}
