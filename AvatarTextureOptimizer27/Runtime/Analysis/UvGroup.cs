using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// All textures that share one UV layout must occupy identical atlas slots.
    /// 同一 UV 对应的全部贴图必须在各图集上位置一致。
    /// </summary>
    public sealed class UvGroup
    {
        public string Id;
        public int UvChannel;
        public Mesh SourceMesh;
        public Renderer SourceRenderer;
        public List<Texture2D> Textures = new List<Texture2D>();
        public List<AtoTextureSemantic> Semantics = new List<AtoTextureSemantic>();
        public List<UvIsland> Islands = new List<UvIsland>();
        public TextureTypeGroup TypeGroup;
        public bool Whitelisted;
        public bool CrossesWrapSeam;
        public Vector2 NormalizeOffset;
        public bool NeedsNormalize;
        public AtoAlphaMode StrictestAlpha = AtoAlphaMode.Opaque;
        public float StrictestCutoff = 0.5f;
        public float MaxWorldScale = 1f;
    }

    /// <summary>
    /// Textures that share extra maps / color space / filter must atlas together.
    /// 共享附加贴图、色彩空间、过滤模式的纹理同一类型组。
    /// </summary>
    public sealed class TextureTypeGroup
    {
        public string Id;
        public bool HasNormal;
        public bool HasMask;
        public bool Srgb;
        public FilterMode Filter;
        public List<UvGroup> Members = new List<UvGroup>();
        public List<Texture2D> Atlases = new List<Texture2D>();
    }
}
