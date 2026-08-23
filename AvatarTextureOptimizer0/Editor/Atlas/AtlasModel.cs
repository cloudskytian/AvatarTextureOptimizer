using System;
using System.Collections.Generic;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    internal sealed class AtlasPlan
    {
        public readonly List<AtlasPage> Pages = new List<AtlasPage>();
        public readonly Dictionary<UvGroupRecord, AtlasGroupLayout> GroupLayouts = new Dictionary<UvGroupRecord, AtlasGroupLayout>();
    }

    internal sealed class AtlasPage
    {
        public int Id;
        public Vector2Int Size;
        public string LayoutSignature;
        public readonly List<UvGroupRecord> Groups = new List<UvGroupRecord>();
        public readonly List<AtlasPlacement> Placements = new List<AtlasPlacement>();
    }

    internal sealed class AtlasPlacement
    {
        public UvGroupRecord Group;
        public UvIsland Island;
        public RectInt PaddedRect;
        public RectInt ContentRect;
        public bool Rotated;
    }

    internal sealed class AtlasGroupLayout
    {
        public string Signature;
        public readonly List<TextureTypeKey> LayerKeys = new List<TextureTypeKey>();
        public readonly Dictionary<Material, List<AtlasLayerBinding>> MaterialLayers = new Dictionary<Material, List<AtlasLayerBinding>>();
    }

    internal sealed class AtlasLayerBinding
    {
        public string PropertyName;
        public TextureTypeKey Key;
        public TextureBindingRecord Initial;
        public readonly List<TextureBindingRecord> AnimatedValues = new List<TextureBindingRecord>();
    }
}
