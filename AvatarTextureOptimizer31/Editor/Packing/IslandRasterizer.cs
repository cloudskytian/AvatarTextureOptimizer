// IslandRasterizer.cs
// Phase 6: Rasterizes UV islands into bitmasks for efficient bin packing.
// Uses 4px granularity rasterization with Burst-compiled jobs for parallelism.
// 阶段6：将 UV 岛光栅化为位掩码，用于高效装箱。使用 Burst 并行作业。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Fosa.AvatarTextureOptimizer.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Packing
{
    /// <summary>
    /// Burst-compiled job that rasterizes a single island's bounding box into a bitmask.
    /// 使用 Burst 编译的光栅化作业。
    /// </summary>
    [BurstCompile]
    internal struct RasterizeIslandJob : IJob
    {
        [WriteOnly] public NativeArray<ulong> Bitmask;
        public int RasterW;
        public int RasterH;
        public int WordsPerRow;
        public int Padding;

        public void Execute()
        {
            for (int i = 0; i < Bitmask.Length; i++)
                Bitmask[i] = 0;

            for (int ry = Padding; ry < RasterH - Padding; ry++)
            {
                if (ry < 0 || ry >= RasterH) continue;
                for (int rx = Padding; rx < RasterW - Padding; rx++)
                {
                    if (rx < 0 || rx >= RasterW) continue;
                    int wordIdx = ry * WordsPerRow + rx / 64;
                    int bitIdx = rx % 64;
                    if (wordIdx >= 0 && wordIdx < Bitmask.Length)
                        Bitmask[wordIdx] |= (1UL << bitIdx);
                }
            }
        }
    }

    /// <summary>
    /// Burst-compiled job that counts set bits in a bitmask array in parallel.
    /// 使用 Burst 并行计算位掩码中置位的数量。
    /// </summary>
    [BurstCompile]
    internal struct PopCountJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ulong> Input;
        [WriteOnly] public NativeArray<long> Output;

        public void Execute(int index)
        {
            ulong x = Input[index];
            long count = 0;
            while (x != 0)
            {
                x &= x - 1;
                count++;
            }
            Output[index] = count;
        }
    }

    /// <summary>
    /// Rasterizes UV islands into coarse bitmasks at 4px granularity.
    /// Each bit represents one rasterization cell (default 4x4 pixels).
    /// 将 UV 岛以 4px 粒度光栅化为位掩码。
    /// </summary>
    internal sealed class IslandRasterizer
    {
        private readonly List<TextureTypeGroup> _typeGroups;
        private readonly AdvancedSettings _settings;
        private readonly ATOLogger _log;

        internal IslandRasterizer(List<TextureTypeGroup> typeGroups, AdvancedSettings settings, ATOLogger log)
        {
            _typeGroups = typeGroups;
            _settings = settings;
            _log = log;
        }

        internal void Execute()
        {
            int totalIslands = 0;
            foreach (var tg in _typeGroups)
                totalIslands += tg.AllIslands.Count;

            _log.Verbose($"Rasterizing {totalIslands} islands at {_settings.rasterGranularity}px granularity.");

            // Collect all islands for parallel processing
            var allIslands = new List<UVIsland>();
            foreach (var tg in _typeGroups)
                allIslands.AddRange(tg.AllIslands);

            if (_settings.useBurstParallelism && allIslands.Count > 1)
            {
                RasterizeParallel(allIslands);
            }
            else
            {
                foreach (var island in allIslands)
                    RasterizeIsland(island);
            }

            long totalArea = 0;
            foreach (var tg in _typeGroups)
                foreach (var island in tg.AllIslands)
                    totalArea += island.RasterArea;

            _log.Verbose($"Total rasterized area: {totalArea} cells.");
        }

        /// <summary>
        /// Rasterizes all islands in parallel using Burst-compiled jobs.
        /// Schedules one IJob per island, then a parallel PopCount job per island.
        /// 使用 Burst 编译的作业并行光栅化所有岛。
        /// </summary>
        private void RasterizeParallel(List<UVIsland> islands)
        {
            var jobHandles = new NativeArray<JobHandle>(islands.Count, Allocator.Temp);
            var nativeBitmasks = new NativeArray<ulong>[islands.Count];
            var islandMeta = new (int rasterW, int rasterH, int wordsPerRow, int granularity)[islands.Count];

            NativeArray<JobHandle> areaJobs = default;
            NativeArray<long>[] areaOutputs = null;

            try
            {
                // Phase 1: Schedule all rasterization jobs
                for (int i = 0; i < islands.Count; i++)
                {
                    var island = islands[i];
                    int granularity = Mathf.Max(1, island.RasterGranularity);
                    int rasterW = Mathf.Max(1, Mathf.Min(4096,
                        Mathf.CeilToInt(island.ScaledPixelBounds.width / (float)granularity)));
                    int rasterH = Mathf.Max(1, Mathf.Min(4096,
                        Mathf.CeilToInt(island.ScaledPixelBounds.height / (float)granularity)));
                    int wordsPerRow = (rasterW + 63) / 64;
                    int totalWords = rasterH * wordsPerRow;
                    int padding = Mathf.Max(0, Mathf.CeilToInt(granularity / 4f));

                    islandMeta[i] = (rasterW, rasterH, wordsPerRow, granularity);
                    nativeBitmasks[i] = new NativeArray<ulong>(totalWords, Allocator.TempJob);

                    var job = new RasterizeIslandJob
                    {
                        Bitmask = nativeBitmasks[i],
                        RasterW = rasterW,
                        RasterH = rasterH,
                        WordsPerRow = wordsPerRow,
                        Padding = padding,
                    };

                    jobHandles[i] = job.Schedule();
                }

                // Complete all rasterization jobs at once
                JobHandle.CompleteAll(jobHandles);

                // Phase 2: Schedule parallel PopCount jobs for area computation
                areaJobs = new NativeArray<JobHandle>(islands.Count, Allocator.Temp);
                areaOutputs = new NativeArray<long>[islands.Count];

                for (int i = 0; i < islands.Count; i++)
                {
                    // Copy bitmask to managed array for later use (packing)
                    var managedBitmask = new ulong[nativeBitmasks[i].Length];
                    nativeBitmasks[i].CopyTo(managedBitmask);
                    islands[i].RasterBitmask = managedBitmask;
                    islands[i].RasterGranularity = islandMeta[i].granularity;

                    // Schedule parallel PopCount
                    areaOutputs[i] = new NativeArray<long>(nativeBitmasks[i].Length, Allocator.TempJob);
                    var popJob = new PopCountJob
                    {
                        Input = nativeBitmasks[i],
                        Output = areaOutputs[i],
                    };
                    areaJobs[i] = popJob.Schedule(nativeBitmasks[i].Length, Mathf.Max(64, nativeBitmasks[i].Length / 8));
                }

                JobHandle.CompleteAll(areaJobs);

                // Sum areas
                for (int i = 0; i < islands.Count; i++)
                {
                    long area = 0;
                    var output = areaOutputs[i];
                    for (int j = 0; j < output.Length; j++)
                        area += output[j];
                    islands[i].RasterArea = area;
                }

                _log.Verbose($"Parallel rasterization: {islands.Count} islands processed with Burst.");
            }
            finally
            {
                if (jobHandles.IsCreated) jobHandles.Dispose();
                if (areaJobs.IsCreated) areaJobs.Dispose();

                if (nativeBitmasks != null)
                {
                    for (int i = 0; i < nativeBitmasks.Length; i++)
                    {
                        if (nativeBitmasks[i].IsCreated) nativeBitmasks[i].Dispose();
                    }
                }
                if (areaOutputs != null)
                {
                    for (int i = 0; i < areaOutputs.Length; i++)
                    {
                        if (areaOutputs[i].IsCreated) areaOutputs[i].Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// Rasterizes a single island sequentially (fallback when Burst is disabled).
        /// 将单个岛的缩放像素包围盒光栅化为位掩码。
        /// </summary>
        private void RasterizeIsland(UVIsland island)
        {
            int granularity = Mathf.Max(1, island.RasterGranularity);

            int rasterW = Mathf.Max(1, Mathf.Min(4096,
                Mathf.CeilToInt(island.ScaledPixelBounds.width / (float)granularity)));
            int rasterH = Mathf.Max(1, Mathf.Min(4096,
                Mathf.CeilToInt(island.ScaledPixelBounds.height / (float)granularity)));

            int wordsPerRow = (rasterW + 63) / 64;
            var bitmask = new ulong[rasterH * wordsPerRow];
            Array.Clear(bitmask, 0, bitmask.Length);

            int padding = Mathf.Max(0, Mathf.CeilToInt(granularity / 4f));

            for (int ry = padding; ry < rasterH - padding; ry++)
            {
                for (int rx = padding; rx < rasterW - padding; rx++)
                {
                    int wordIdx = ry * wordsPerRow + rx / 64;
                    int bitIdx = rx % 64;
                    if (wordIdx < bitmask.Length)
                        bitmask[wordIdx] |= (1UL << bitIdx);
                }
            }

            long area = 0;
            foreach (var word in bitmask)
                area += PopCount(word);

            island.RasterBitmask = bitmask;
            island.RasterArea = area;
            island.RasterGranularity = granularity;
        }

        private static long PopCount(ulong x)
        {
            long count = 0;
            while (x != 0)
            {
                x &= x - 1;
                count++;
            }
            return count;
        }

        // ──────────────────────────────────────────────
        // Static bitmask operations used by BinPacker
        // 用于装箱的静态位掩码操作
        // ──────────────────────────────────────────────

        /// <summary>
        /// Checks if a bitmask collides with a region of an atlas bitmask at a given position.
        /// 检查位掩码在给定位置是否与图集位掩码碰撞。
        /// </summary>
        internal static bool CheckCollision(ulong[] islandBitmask, int islandRasterW, int islandRasterH,
            ulong[] atlasBitmask, int atlasRasterW, int atlasRasterH,
            int offsetX, int offsetY, int granularity)
        {
            int islandWordsPerRow = (islandRasterW + 63) / 64;
            int atlasWordsPerRow = (atlasRasterW + 63) / 64;

            for (int ry = 0; ry < islandRasterH; ry++)
            {
                int atlasY = ry + offsetY;
                if (atlasY < 0 || atlasY >= atlasRasterH) continue;

                for (int rx = 0; rx < islandRasterW; rx++)
                {
                    int islandWord = ry * islandWordsPerRow + rx / 64;
                    int islandBit = rx % 64;
                    if (islandWord >= islandBitmask.Length) continue;
                    if ((islandBitmask[islandWord] & (1UL << islandBit)) == 0) continue;

                    int atlasX = rx + offsetX;
                    if (atlasX < 0 || atlasX >= atlasRasterW) return true;

                    int atlasWord = atlasY * atlasWordsPerRow + atlasX / 64;
                    int atlasBit = atlasX % 64;
                    if (atlasWord >= atlasBitmask.Length) return true;

                    if ((atlasBitmask[atlasWord] & (1UL << atlasBit)) != 0)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Stamps an island bitmask into an atlas bitmask at a given position.
        /// 将岛位掩码盖章到图集位掩码的给定位置。
        /// </summary>
        internal static void Stamp(ulong[] islandBitmask, int islandRasterW, int islandRasterH,
            ulong[] atlasBitmask, int atlasRasterW, int atlasRasterH,
            int offsetX, int offsetY)
        {
            int islandWordsPerRow = (islandRasterW + 63) / 64;
            int atlasWordsPerRow = (atlasRasterW + 63) / 64;

            for (int ry = 0; ry < islandRasterH; ry++)
            {
                int atlasY = ry + offsetY;
                if (atlasY < 0 || atlasY >= atlasRasterH) continue;

                for (int rx = 0; rx < islandRasterW; rx++)
                {
                    int islandWord = ry * islandWordsPerRow + rx / 64;
                    int islandBit = rx % 64;
                    if (islandWord >= islandBitmask.Length) continue;
                    if ((islandBitmask[islandWord] & (1UL << islandBit)) == 0) continue;

                    int atlasX = rx + offsetX;
                    if (atlasX < 0 || atlasX >= atlasRasterW) continue;

                    int atlasWord = atlasY * atlasWordsPerRow + atlasX / 64;
                    int atlasBit = atlasX % 64;
                    if (atlasWord < atlasBitmask.Length)
                        atlasBitmask[atlasWord] |= (1UL << atlasBit);
                }
            }
        }

        /// <summary>
        /// Transposes a bitmask (used for 90-degree rotation during packing).
        /// Normal map tangent data is never rotated.
        /// 转置位掩码（用于装箱时的 90 度旋转）。
        /// </summary>
        internal static (ulong[], int, int) Transpose(ulong[] bitmask, int w, int h)
        {
            int newW = h;
            int newH = w;
            int newWordsPerRow = (newW + 63) / 64;
            int oldWordsPerRow = (w + 63) / 64;

            var result = new ulong[newH * newWordsPerRow];
            Array.Clear(result, 0, result.Length);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int oldWord = y * oldWordsPerRow + x / 64;
                    int oldBit = x % 64;
                    if (oldWord >= bitmask.Length) continue;
                    if ((bitmask[oldWord] & (1UL << oldBit)) == 0) continue;

                    int newWord = x * newWordsPerRow + y / 64;
                    int newBit = y % 64;
                    if (newWord < result.Length)
                        result[newWord] |= (1UL << newBit);
                }
            }

            return (result, newW, newH);
        }
    }
}
