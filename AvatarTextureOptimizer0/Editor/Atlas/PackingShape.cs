using System;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Quality;
using Unity.Collections;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    internal sealed class PackingShape
    {
        public UvGroupRecord Group;
        public UvIsland Island;
        public int Width;
        public int Height;
        public byte[] Bits;

        public bool IsSet(int x, int y)
        {
            var index = y * Width + x;
            return (Bits[index >> 2] & (1 << (index & 3))) != 0;
        }

        public PackingShape Rotated()
        {
            var value = new PackingShape { Group = Group, Island = Island, Width = Height, Height = Width,
                Bits = new byte[(Width * Height + 3) / 4] };
            for (var y = 0; y < Height; y++) for (var x = 0; x < Width; x++)
                if (IsSet(x, y)) Set(value.Bits, value.Width, Height - 1 - y, x);
            return value;
        }

        public static PackingShape Build(UvGroupRecord group, UvIsland island, int padding)
        {
            var content = IslandMaskRasterizer.Rasterize(group, island, island.TargetPixelSize, Allocator.TempJob);
            try
            {
                var width = Align4(island.TargetPixelSize.x + padding * 2);
                var height = Align4(island.TargetPixelSize.y + padding * 2);
                var result = new PackingShape { Group = group, Island = island, Width = width, Height = height,
                    Bits = new byte[(width * height + 3) / 4] };
                for (var y = 0; y < island.TargetPixelSize.y; y++) for (var x = 0; x < island.TargetPixelSize.x; x++)
                {
                    var source = y * island.TargetPixelSize.x + x;
                    if ((content[source >> 2] & (1 << (source & 3))) == 0) continue;
                    for (var oy = -padding; oy <= padding; oy++) for (var ox = -padding; ox <= padding; ox++)
                    {
                        var px = x + padding + ox; var py = y + padding + oy;
                        if (px >= 0 && py >= 0 && px < width && py < height) Set(result.Bits, width, px, py);
                    }
                }
                return result;
            }
            finally { content.Dispose(); }
        }

        private static int Align4(int value) => (value + 3) & ~3;
        private static void Set(byte[] bits, int width, int x, int y)
        {
            var index = y * width + x; bits[index >> 2] |= (byte)(1 << (index & 3));
        }
    }
}
