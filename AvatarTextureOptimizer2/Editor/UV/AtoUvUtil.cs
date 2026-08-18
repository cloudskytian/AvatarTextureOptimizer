using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class AtoUvUtil
    {
        public static Vector2[] GetUv(Mesh mesh, int channel)
        {
            var list = new List<Vector2>();
            mesh.GetUVs(channel, list);
            return list.ToArray();
        }

        /// <summary>
        /// True if UVs can be integer-translated into [0,1] without crossing wrap.
        /// 若可整体平移归一到 [0,1] 且不跨 wrap 缝则返回 true。
        /// </summary>
        public static bool CanNormalize(Mesh mesh, int channel, out string reason)
        {
            reason = null;
            var uv = GetUv(mesh, channel);
            if (uv == null || uv.Length == 0)
            {
                reason = "no-uv";
                return false;
            }
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            for (int i = 0; i < uv.Length; i++)
            {
                minX = Mathf.Min(minX, uv[i].x);
                minY = Mathf.Min(minY, uv[i].y);
                maxX = Mathf.Max(maxX, uv[i].x);
                maxY = Mathf.Max(maxY, uv[i].y);
            }
            var spanX = maxX - minX;
            var spanY = maxY - minY;
            if (spanX > 1.0001f || spanY > 1.0001f)
            {
                reason = "cross-wrap";
                return false;
            }
            return true;
        }

        public static Vector2[] Normalize(Vector2[] uv, out Vector2 translation)
        {
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            for (int i = 0; i < uv.Length; i++)
            {
                minX = Mathf.Min(minX, uv[i].x);
                minY = Mathf.Min(minY, uv[i].y);
            }
            var tx = Mathf.Floor(minX);
            var ty = Mathf.Floor(minY);
            translation = new Vector2(tx, ty);
            var o = new Vector2[uv.Length];
            for (int i = 0; i < uv.Length; i++)
                o[i] = uv[i] - translation;
            return o;
        }
    }

    public sealed class AtoIsland
    {
        public int Id;
        public Mesh Mesh;
        public int UvChannel;
        public int Submesh;
        public Texture2D Source;
        public AtoTextureRole Role;
        public AtoBlendMode Blend;
        public float Cutoff;
        public List<int> Vertices = new List<int>();
        public List<int> Triangles = new List<int>();
        public Rect UvBounds;
        public Rect PixelBounds;
        public float WorldArea;
        public float ScaleU = 1f;
        public float ScaleV = 1f;
        public bool SolidColor;
        public Color32 Solid;
        public int UvGroupId;
        public AtoTypeGroupKey TypeKey;
        public bool Eligible = true;
        public Texture2D Cropped;
        public int RasterW;
        public int RasterH;
        public ulong[] Mask;
        public int AtlasX, AtlasY;
        public bool Rotated;
        public Texture2D Atlas;
        public int AtlasSizeX, AtlasSizeY;
    }
}
