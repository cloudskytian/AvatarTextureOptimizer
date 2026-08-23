using System;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Quality
{
    internal static class IslandMaskRasterizer
    {
        internal const int BruteTriangleThreshold = 64;
        private const int TileSize = 8;
        private const int MaximumTriangleReferences = 4 * 1024 * 1024;

        public static NativeArray<byte> Rasterize(UvGroupRecord group, UvIsland island, Vector2Int size,
            Allocator allocator) => Rasterize(group, island, size, allocator, out _);

        // The out value is an internal regression-test seam: false means the bounded index deliberately fell back
        // to the allocation-free brute path. / out 值仅供回归测试确认预算超限时确实走无额外索引的保守路径。
        internal static NativeArray<byte> Rasterize(UvGroupRecord group, UvIsland island, Vector2Int size,
            Allocator allocator, out bool usedTriangleBins)
        {
            if (group == null || group.Renderer == null || group.Renderer.Mesh == null)
                throw new ArgumentNullException(nameof(group));
            if (island == null) throw new ArgumentNullException(nameof(island));
            if (size.x <= 0 || size.y <= 0) throw new ArgumentOutOfRangeException(nameof(size));
            if (!(island.UvBounds.width > 0f) || !(island.UvBounds.height > 0f) ||
                (float.IsNaN(island.UvBounds.width) || float.IsInfinity(island.UvBounds.width)) ||
                (float.IsNaN(island.UvBounds.height) || float.IsInfinity(island.UvBounds.height)))
                throw new InvalidOperationException("ATO cannot rasterize a non-finite or degenerate UV-island bound.");

            var mesh = group.Renderer.Mesh;
            var triangleIndices = mesh.GetTriangles(group.Slot.Slot);
            var uvs = new System.Collections.Generic.List<Vector2>();
            mesh.GetUVs(group.UvChannel, uvs);
            var triangles = new NativeArray<float2>(island.TriangleIndices.Count * 3, Allocator.TempJob);
            NativeArray<byte> mask = default;
            NativeArray<int> tileOffsets = default;
            NativeArray<int> tileTriangleIndices = default;
            usedTriangleBins = false;
            try
            {
                var cursor = 0;
                foreach (var ordinal in island.TriangleIndices)
                {
                    if ((cursor & 4095) == 0) ATOProgress.Checkpoint("Preparing UV-island mask triangles");
                    if (ordinal < 0 || ordinal > triangleIndices.Length / 3 - 1)
                        throw new InvalidOperationException("ATO UV island references an invalid triangle ordinal.");
                    for (var corner = 0; corner < 3; corner++)
                    {
                        var vertex = triangleIndices[ordinal * 3 + corner];
                        if (vertex < 0 || vertex >= uvs.Count)
                            throw new InvalidOperationException("ATO mesh triangle references an invalid UV vertex.");
                        var uv = uvs[vertex] + island.IntegerNormalization;
                        var transformed = new float2(
                            (uv.x - island.UvBounds.xMin) / island.UvBounds.width * size.x,
                            (uv.y - island.UvBounds.yMin) / island.UvBounds.height * size.y);
                        if (!math.all(math.isfinite(transformed)))
                            throw new InvalidOperationException("ATO UV-island mask contains non-finite coordinates.");
                        triangles[cursor++] = transformed;
                    }
                }

                var pixelCount = checked(size.x * size.y);
                if (island.TriangleIndices.Count > BruteTriangleThreshold)
                    usedTriangleBins = TryBuildTriangleBins(triangles, size.x, size.y, pixelCount,
                        out tileOffsets, out tileTriangleIndices);
                if (!usedTriangleBins)
                {
                    // Jobs validate every NativeArray field even when a branch does not read it.
                    tileOffsets = new NativeArray<int>(1, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                    tileTriangleIndices = new NativeArray<int>(1, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                }

                mask = new NativeArray<byte>((pixelCount + 3) / 4, allocator, NativeArrayOptions.ClearMemory);
                const int cancellableBatchLength = 4096;
                for (var start = 0; start < mask.Length; start += cancellableBatchLength)
                {
                    ATOProgress.Checkpoint("Rasterizing UV-island mask");
                    var count = math.min(cancellableBatchLength, mask.Length - start);
                    new Rasterize4PixelJob
                    {
                        Triangles = triangles,
                        TileOffsets = tileOffsets,
                        TileTriangleIndices = tileTriangleIndices,
                        Width = size.x,
                        Height = size.y,
                        TilesX = size.x / TileSize + (size.x % TileSize == 0 ? 0 : 1),
                        UseTriangleBins = usedTriangleBins ? 1 : 0,
                        StartIndex = start,
                        Mask = mask
                    }.Schedule(count, 64).Complete();
                }
                var completed = mask; mask = default; return completed;
            }
            finally
            {
                if (mask.IsCreated) mask.Dispose();
                if (tileTriangleIndices.IsCreated) tileTriangleIndices.Dispose();
                if (tileOffsets.IsCreated) tileOffsets.Dispose();
                if (triangles.IsCreated) triangles.Dispose();
            }
        }

        private static bool TryBuildTriangleBins(NativeArray<float2> triangles, int width, int height,
            int pixelCount, out NativeArray<int> offsets, out NativeArray<int> references)
        {
            offsets = default; references = default;
            var tilesX = checked(width / TileSize + (width % TileSize == 0 ? 0 : 1));
            var tilesY = checked(height / TileSize + (height % TileSize == 0 ? 0 : 1));
            var tileCount = checked(tilesX * tilesY);
            var counts = new NativeArray<int>(tileCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            NativeArray<int> cursors = default;
            try
            {
                var budget = TriangleReferenceBudget(pixelCount);
                var referenceCount = 0;
                for (var triangle = 0; triangle < triangles.Length / 3; triangle++)
                {
                    if ((triangle & 255) == 0) ATOProgress.Checkpoint("Indexing UV-mask triangle bounds");
                    if (!TryGetTileRange(triangles, triangle, width, height, tilesX, tilesY,
                            out var minX, out var maxX, out var minY, out var maxY)) continue;
                    for (var y = minY; y <= maxY; y++)
                    for (var x = minX; x <= maxX; x++)
                    {
                        if (referenceCount >= budget) return false;
                        counts[y * tilesX + x]++;
                        referenceCount++;
                    }
                }

                offsets = new NativeArray<int>(tileCount + 1, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                for (var tile = 0; tile < tileCount; tile++)
                {
                    if ((tile & 4095) == 0) ATOProgress.Checkpoint("Building UV-mask tile offsets");
                    offsets[tile + 1] = offsets[tile] + counts[tile];
                }
                references = new NativeArray<int>(referenceCount, Allocator.TempJob,
                    NativeArrayOptions.UninitializedMemory);
                cursors = new NativeArray<int>(tileCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                for (var tile = 0; tile < tileCount; tile++) cursors[tile] = offsets[tile];

                for (var triangle = 0; triangle < triangles.Length / 3; triangle++)
                {
                    if ((triangle & 255) == 0) ATOProgress.Checkpoint("Filling UV-mask triangle bins");
                    if (!TryGetTileRange(triangles, triangle, width, height, tilesX, tilesY,
                            out var minX, out var maxX, out var minY, out var maxY)) continue;
                    for (var y = minY; y <= maxY; y++)
                    for (var x = minX; x <= maxX; x++)
                    {
                        var tile = y * tilesX + x;
                        var destination = cursors[tile];
                        references[destination] = triangle;
                        cursors[tile] = destination + 1;
                    }
                }
                return true;
            }
            catch
            {
                if (references.IsCreated) references.Dispose();
                if (offsets.IsCreated) offsets.Dispose();
                references = default; offsets = default; throw;
            }
            finally
            {
                if (cursors.IsCreated) cursors.Dispose();
                counts.Dispose();
            }
        }

        private static int TriangleReferenceBudget(int pixelCount)
        {
            var scaled = Math.Max(4096L, (long)pixelCount * 4L);
            return (int)Math.Min(MaximumTriangleReferences, scaled);
        }

        private static bool TryGetTileRange(NativeArray<float2> triangles, int triangle, int width, int height,
            int tilesX, int tilesY, out int minTileX, out int maxTileX, out int minTileY, out int maxTileY)
        {
            var offset = triangle * 3;
            var a = triangles[offset]; var b = triangles[offset + 1]; var c = triangles[offset + 2];
            var minimum = math.min(a, math.min(b, c));
            var maximum = math.max(a, math.max(b, c));
            if (maximum.x < 0f || maximum.y < 0f || minimum.x > width || minimum.y > height)
            {
                minTileX = minTileY = 0; maxTileX = maxTileY = -1; return false;
            }
            // One-pixel expansion preserves triangles lying exactly on a pixel/tile edge; false-positive bin entries
            // are harmless, while a false negative would erase coverage.
            var minimumPixel = math.clamp(math.floor(minimum) - 1f, new float2(0f), new float2(width - 1, height - 1));
            var maximumPixel = math.clamp(math.floor(maximum) + 1f, new float2(0f), new float2(width - 1, height - 1));
            minTileX = math.clamp((int)minimumPixel.x / TileSize, 0, tilesX - 1);
            maxTileX = math.clamp((int)maximumPixel.x / TileSize, 0, tilesX - 1);
            minTileY = math.clamp((int)minimumPixel.y / TileSize, 0, tilesY - 1);
            maxTileY = math.clamp((int)maximumPixel.y / TileSize, 0, tilesY - 1);
            return minTileX <= maxTileX && minTileY <= maxTileY;
        }

        public static bool IsSet(NativeArray<byte> mask, int pixel) =>
            (mask[pixel >> 2] & (1 << (pixel & 3))) != 0;

        [BurstCompile]
        private struct Rasterize4PixelJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> Triangles;
            [ReadOnly] public NativeArray<int> TileOffsets;
            [ReadOnly] public NativeArray<int> TileTriangleIndices;
            public int Width;
            public int Height;
            public int TilesX;
            public int UseTriangleBins;
            public int StartIndex;
            [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<byte> Mask;

            public void Execute(int index)
            {
                index += StartIndex;
                byte bits = 0;
                for (var bit = 0; bit < 4; bit++)
                {
                    var pixel = index * 4 + bit;
                    if (pixel >= Width * Height) break;
                    var minimum = new float2(pixel % Width, pixel / Width);
                    if (UseTriangleBins != 0)
                    {
                        var tile = (pixel / Width / TileSize) * TilesX + pixel % Width / TileSize;
                        for (var cursor = TileOffsets[tile]; cursor < TileOffsets[tile + 1]; cursor++)
                        {
                            var triangle = TileTriangleIndices[cursor] * 3;
                            if (!TriangleTouchesPixel(minimum, Triangles[triangle], Triangles[triangle + 1],
                                    Triangles[triangle + 2])) continue;
                            bits |= (byte)(1 << bit); break;
                        }
                    }
                    else
                    {
                        for (var triangle = 0; triangle < Triangles.Length; triangle += 3)
                        {
                            if (!TriangleTouchesPixel(minimum, Triangles[triangle], Triangles[triangle + 1],
                                    Triangles[triangle + 2])) continue;
                            bits |= (byte)(1 << bit); break;
                        }
                    }
                }
                Mask[index] = bits;
            }

            private static bool TriangleTouchesPixel(float2 minimum, float2 a, float2 b, float2 c)
            {
                var maximum = minimum + 1f;
                if (VertexInPixel(a, minimum, maximum) || VertexInPixel(b, minimum, maximum) ||
                    VertexInPixel(c, minimum, maximum)) return true;
                var p0 = minimum; var p1 = new float2(maximum.x, minimum.y); var p2 = maximum;
                var p3 = new float2(minimum.x, maximum.y);
                if (Inside(p0, a, b, c) || Inside(p1, a, b, c) || Inside(p2, a, b, c) ||
                    Inside(p3, a, b, c)) return true;
                return Segment(a, b, p0, p1) || Segment(a, b, p1, p2) || Segment(a, b, p2, p3) ||
                       Segment(a, b, p3, p0) || Segment(b, c, p0, p1) || Segment(b, c, p1, p2) ||
                       Segment(b, c, p2, p3) || Segment(b, c, p3, p0) || Segment(c, a, p0, p1) ||
                       Segment(c, a, p1, p2) || Segment(c, a, p2, p3) || Segment(c, a, p3, p0);
            }

            private static bool VertexInPixel(float2 value, float2 minimum, float2 maximum) =>
                value.x >= minimum.x && value.y >= minimum.y && value.x <= maximum.x && value.y <= maximum.y;

            private static bool Segment(float2 a, float2 b, float2 c, float2 d)
            {
                var abC = Cross(b - a, c - a); var abD = Cross(b - a, d - a);
                var cdA = Cross(d - c, a - c); var cdB = Cross(d - c, b - c);
                return abC * abD <= 1e-5f && cdA * cdB <= 1e-5f &&
                       math.max(math.min(a.x, b.x), math.min(c.x, d.x)) <=
                       math.min(math.max(a.x, b.x), math.max(c.x, d.x)) + 1e-5f &&
                       math.max(math.min(a.y, b.y), math.min(c.y, d.y)) <=
                       math.min(math.max(a.y, b.y), math.max(c.y, d.y)) + 1e-5f;
            }

            private static bool Inside(float2 p, float2 a, float2 b, float2 c)
            {
                var d1 = Cross(p - b, a - b); var d2 = Cross(p - c, b - c); var d3 = Cross(p - a, c - a);
                var negative = d1 < -1e-5f || d2 < -1e-5f || d3 < -1e-5f;
                var positive = d1 > 1e-5f || d2 > 1e-5f || d3 > 1e-5f;
                return !(negative && positive);
            }

            private static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;
        }
    }
}
