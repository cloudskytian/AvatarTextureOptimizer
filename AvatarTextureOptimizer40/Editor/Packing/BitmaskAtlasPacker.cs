using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.Ato.Editor.Packing
{
    /// <summary>
    /// Raster-shape atlas packer.
    ///  - 4px-granularity occupancy bitmask (one bit per cell, rows of 64-bit words).
    ///  - Full-scan BLF (Bottom-Left-Fill): scan y then x for the first fit.
    ///  - Sorted area-desc then edge-length-desc by caller; 90° rotation via bitmask transpose.
    ///    Normal maps disable rotation (rotating tangent-space normals is incorrect; we NEVER
    ///    recompute tangent-space normals).
    ///  - Packs by actual raster footprint, not by AABB.
    /// 位图形状装箱器：4px 粒度位掩码 + 全扫描 BLF + 90°旋转（法线贴图禁用旋转，绝不重算法线），
    /// 按光栅实际形状装箱。
    /// </summary>
    internal sealed class BitmaskAtlasPacker : IDisposable
    {
        public const int Cell = 4;
        public readonly int Width, Height;
        private readonly int _gw, _gh;
        private readonly int _wordsPerRow;
        private readonly NativeArray<ulong> _mask;

        public BitmaskAtlasPacker(int width, int height, Allocator allocator)
        {
            Width = width; Height = height;
            _gw = (width + Cell - 1) / Cell;
            _gh = (height + Cell - 1) / Cell;
            _wordsPerRow = (_gw + 63) >> 6;
            _mask = new NativeArray<ulong>(_gh * _wordsPerRow, allocator);
        }

        public bool TryPlace(NativeArray<ulong> islandMask, int islandGW, int islandGH,
            int paddingCells, bool allowRotation, out RectInt rect, out bool rotated)
        {
            if (TryPlace(islandMask, islandGW, islandGH, paddingCells, out rect))
            { rotated = false; return true; }
            if (allowRotation)
            {
                var transposed = Transpose(islandMask, islandGW, islandGH, out int tGW, out int tGH);
                try
                {
                    if (TryPlace(transposed, tGW, tGH, paddingCells, out rect))
                    { rotated = true; return true; }
                }
                finally { if (transposed.IsCreated) transposed.Dispose(); }
            }
            rect = default; rotated = false; return false;
        }

        private bool TryPlace(NativeArray<ulong> islandMask, int gw, int gh, int pad, out RectInt rect)
        {
            int spanW = gw + pad, spanH = gh + pad;
            if (spanW > _gw || spanH > _gh) { rect = default; return false; }

            for (int y = 0; y + spanH <= _gh; y++)
                for (int x = 0; x + spanW <= _gw; x++)
                {
                    if (FitsAt(islandMask, gw, gh, x, y, pad))
                    {
                        Stamp(islandMask, gw, gh, x, y);
                        rect = new RectInt(x * Cell, y * Cell, gw * Cell, gh * Cell);
                        return true;
                    }
                }
            rect = default; return false;
        }

        private bool FitsAt(NativeArray<ulong> island, int gw, int gh, int ox, int oy, int pad)
        {
            // Atlas must be empty everywhere inside island footprint + padding.
            // 岛占用区与 padding 在图集内都必须为空。
            int spanW = gw + pad, spanH = gh + pad;
            for (int y = 0; y < spanH; y++)
            {
                int my = oy + y;
                for (int x = 0; x < spanW; x++)
                {
                    int mx = ox + x;
                    if (mx < 0 || my < 0 || mx >= _gw || my >= _gh) return false;
                    if (GetCell(_mask, mx, my, _wordsPerRow)) return false;
                }
            }
            return true;
        }

        private void Stamp(NativeArray<ulong> island, int gw, int gh, int ox, int oy)
        {
            int islandWords = (gw + 63) >> 6;
            for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                    if (GetCell(island, x, y, islandWords))
                        SetCell(_mask, ox + x, oy + y);
        }

        private static bool GetCell(NativeArray<ulong> arr, int x, int y, int wordsPerRow)
        {
            int idx = y * wordsPerRow + (x >> 6);
            if (idx < 0 || idx >= arr.Length) return false;
            return (arr[idx] & (1UL << (x & 63))) != 0;
        }
        private void SetCell(NativeArray<ulong> arr, int x, int y)
        {
            int idx = y * _wordsPerRow + (x >> 6);
            if (idx >= 0 && idx < arr.Length) arr[idx] |= 1UL << (x & 63);
        }

        private static NativeArray<ulong> Transpose(NativeArray<ulong> src, int gw, int gh, out int tGW, out int tGH)
        {
            tGW = gh; tGH = gw;
            int srcWords = (gw + 63) >> 6;
            int dstWords = (tGW + 63) >> 6;
            var dst = new NativeArray<ulong>(tGH * dstWords, Allocator.TempJob);
            for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                {
                    if (!GetCell(src, x, y, srcWords)) continue;
                    int nx = y, ny = x;
                    SetCellExternal(dst, nx, ny, dstWords);
                }
            return dst;
        }

        private static void SetCellExternal(NativeArray<ulong> arr, int x, int y, int wordsPerRow)
        {
            int idx = y * wordsPerRow + (x >> 6);
            if (idx >= 0 && idx < arr.Length) arr[idx] |= 1UL << (x & 63);
        }

        public float Utilization()
        {
            long on = 0, total = (long)_gw * _gh;
            for (int i = 0; i < _mask.Length; i++) on += math.countbits(_mask[i]);
            return total > 0 ? (float)on / total : 0f;
        }

        public void Dispose() { if (_mask.IsCreated) _mask.Dispose(); }
    }
}
