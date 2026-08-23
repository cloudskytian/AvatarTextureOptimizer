using System;
using System.Collections.Generic;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>EN: Burst rasterization into a 4-pixel-granularity row bitmask. ZH: Burst 光栅化到 4 像素粒度的逐行位掩码。</summary>
    internal static class RasterMaskBuilder
    {
        internal const int Granularity = 4;

        [BurstCompile(FloatMode.Fast, FloatPrecision.Standard)]
        private struct RasterizeJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> Triangles;
            [WriteOnly] public NativeArray<byte> Cells;
            public int Width;
            public int Height;
            public void Execute(int index)
            {
                var x = index % Width; var y = index / Width;
                var point = new float2((x + 0.5f) / Width, (y + 0.5f) / Height);
                byte occupied = 0;
                for (var i = 0; i + 2 < Triangles.Length; i += 3)
                {
                    if (Inside(point, Triangles[i], Triangles[i + 1], Triangles[i + 2])) { occupied = 1; break; }
                }
                Cells[index] = occupied;
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

        public static RasterMask Build(UvGroup group, UvIsland island)
        {
            var width = Mathf.Max(1, Mathf.CeilToInt(island.TargetPixelSize.x / (float)Granularity));
            var height = Mathf.Max(1, Mathf.CeilToInt(island.TargetPixelSize.y / (float)Granularity));
            var uv = new List<Vector2>(); group.Renderer.SourceMesh.GetUVs(group.UvChannel, uv);
            using (var triangles = new NativeArray<float2>(island.Triangles.Count * 3, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
            using (var cells = new NativeArray<byte>(width * height, Allocator.TempJob, NativeArrayOptions.ClearMemory))
            {
                var cursor = 0;
                foreach (var triangle in island.Triangles)
                {
                    Add(triangle.A); Add(triangle.B); Add(triangle.C);
                }
                void Add(int vertex)
                {
                    var point = uv[vertex] + group.IntegerTranslation;
                    triangles[cursor++] = new float2(
                        (point.x - island.UvBounds.x) / Mathf.Max(1e-8f, island.UvBounds.width),
                        (point.y - island.UvBounds.y) / Mathf.Max(1e-8f, island.UvBounds.height));
                }
                new RasterizeJob { Triangles = triangles, Cells = cells, Width = width, Height = height }
                    .Schedule(cells.Length, 128).Complete();
                var mask = FromCells(cells, width, height);
                // EN: Preserve very thin triangles that miss every cell center.
                // ZH: 保留未命中任何单元中心的极细三角形。
                if (mask.SetBitCount == 0) Set(mask, width / 2, height / 2);
                mask.Rotated = Rotate(mask);
                mask.Rotated.Rotated = mask;
                return mask;
            }
        }

        public static RasterMask Pad(RasterMask source, int cells)
        {
            if (cells <= 0) return source;
            var output = Create(source.Width + cells * 2, source.Height + cells * 2);
            for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                if (!Get(source, x, y)) continue;
                for (var dy = -cells; dy <= cells; dy++)
                for (var dx = -cells; dx <= cells; dx++)
                    if (dx * dx + dy * dy <= cells * cells) Set(output, x + cells + dx, y + cells + dy);
            }
            return output;
        }

        public static RasterMask Rotate(RasterMask source)
        {
            var output = Create(source.Height, source.Width);
            for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
                if (Get(source, x, y)) Set(output, source.Height - 1 - y, x);
            return output;
        }

        public static bool Get(RasterMask mask, int x, int y)
        {
            if (x < 0 || y < 0 || x >= mask.Width || y >= mask.Height) return false;
            return (mask.Rows[y * mask.Stride + (x >> 6)] & (1UL << (x & 63))) != 0;
        }

        public static void Set(RasterMask mask, int x, int y)
        {
            if (x < 0 || y < 0 || x >= mask.Width || y >= mask.Height) return;
            var index = y * mask.Stride + (x >> 6); var bit = 1UL << (x & 63);
            if ((mask.Rows[index] & bit) == 0) { mask.Rows[index] |= bit; mask.SetBitCount++; }
        }

        public static RasterMask Create(int width, int height)
        {
            var stride = (width + 63) >> 6;
            return new RasterMask { Width = width, Height = height, Stride = stride, Rows = new ulong[stride * height] };
        }

        private static RasterMask FromCells(NativeArray<byte> cells, int width, int height)
        {
            var output = Create(width, height);
            for (var i = 0; i < cells.Length; i++) if (cells[i] != 0) Set(output, i % width, i / width);
            return output;
        }
    }
}
