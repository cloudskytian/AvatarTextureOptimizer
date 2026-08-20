using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>Connected UV island in one UV channel. / 单 UV 通道上的连通岛。</summary>
    public sealed class UvIsland
    {
        public int MeshId;
        public int Submesh;
        public int UvChannel;
        public List<int> TriangleIndices = new List<int>();
        public Rect Bounds01;
        public RectInt PixelBounds;
        public float WorldArea;
        public bool IsSolidColor;
        public Color SolidColor;
        public float Anisotropy = 1f;
        public float ScaleU = 1f;
        public float ScaleV = 1f;
        public bool SkipAtlas;
        public string Reason;
    }
}
