using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    public struct PackedIsland
    {
        public UvIsland Island;
        public int X;
        public int Y;
        public int W;
        public int H;
        public bool Rotated;
        public Texture2D Source;
    }

    /// <summary>
    /// Full-scan Bottom-Left-Fill on raster bitmasks (not rectangle packing).
    /// 在光栅位掩码上做全扫描 BLF（不是矩形装箱）。
    /// </summary>
    public sealed class BlfPacker : IDisposable
    {
        public int Width;
        public int Height;
        public int PadCells;
        BitmaskRaster.Mask _occ;
        bool _inited;

        public void Reset(int widthPx, int heightPx, int paddingPx)
        {
            Dispose();
            Width = widthPx;
            Height = heightPx;
            PadCells = Mathf.Max(0, (paddingPx + BitmaskRaster.Granularity - 1) / BitmaskRaster.Granularity);
            var cw = Math.Max(1, (widthPx + BitmaskRaster.Granularity - 1) / BitmaskRaster.Granularity);
            var ch = Math.Max(1, (heightPx + BitmaskRaster.Granularity - 1) / BitmaskRaster.Granularity);
            var stride = (cw + 63) >> 6;
            _occ = new BitmaskRaster.Mask
            {
                CellsW = cw,
                CellsH = ch,
                Bits = new NativeArray<ulong>(stride * ch, Allocator.Persistent)
            };
            _inited = true;
        }

        public bool TryPlace(BitmaskRaster.Mask shape, bool allowRotate, out int xPx, out int yPx, out bool rotated,
            out BitmaskRaster.Mask used)
        {
            xPx = yPx = 0;
            rotated = false;
            used = shape;
            if (!_inited) return false;

            if (TryPlaceOne(shape, out xPx, out yPx))
            {
                Stamp(shape, xPx, yPx);
                return true;
            }

            if (allowRotate)
            {
                var rot = BitmaskRaster.Rotate90(shape, Allocator.TempJob);
                if (TryPlaceOne(rot, out xPx, out yPx))
                {
                    Stamp(rot, xPx, yPx);
                    rotated = true;
                    used = rot;
                    return true;
                }

                rot.Dispose();
            }

            return false;
        }

        bool TryPlaceOne(BitmaskRaster.Mask shape, out int xPx, out int yPx)
        {
            xPx = yPx = 0;
            var maxX = _occ.CellsW - shape.CellsW - PadCells;
            var maxY = _occ.CellsH - shape.CellsH - PadCells;
            if (maxX < PadCells || maxY < PadCells) return false;

            for (int y = PadCells; y <= maxY; y++)
            for (int x = PadCells; x <= maxX; x++)
            {
                if (Fits(shape, x, y))
                {
                    xPx = x * BitmaskRaster.Granularity;
                    yPx = y * BitmaskRaster.Granularity;
                    return true;
                }
            }

            return false;
        }

        bool Fits(BitmaskRaster.Mask shape, int ox, int oy)
        {
            for (int y = 0; y < shape.CellsH; y++)
            for (int x = 0; x < shape.CellsW; x++)
            {
                if (!BitmaskRaster.Test(shape, x, y)) continue;
                // Include padding halo. / 含 padding 光晕。
                for (int dy = -PadCells; dy <= PadCells; dy++)
                for (int dx = -PadCells; dx <= PadCells; dx++)
                {
                    if (BitmaskRaster.Test(_occ, ox + x + dx, oy + y + dy)) return false;
                }
            }

            return true;
        }

        void Stamp(BitmaskRaster.Mask shape, int xPx, int yPx)
        {
            var ox = xPx / BitmaskRaster.Granularity;
            var oy = yPx / BitmaskRaster.Granularity;
            for (int y = 0; y < shape.CellsH; y++)
            for (int x = 0; x < shape.CellsW; x++)
            {
                if (BitmaskRaster.Test(shape, x, y))
                    BitmaskRaster.Set(_occ, ox + x, oy + y);
            }
        }

        public float Utilization()
        {
            if (!_inited) return 0f;
            var occ = BitmaskRaster.OccupiedCells(_occ);
            var tot = _occ.CellsW * _occ.CellsH;
            return tot == 0 ? 0f : (float)occ / tot;
        }

        public void Dispose()
        {
            if (_inited)
            {
                _occ.Dispose();
                _inited = false;
            }
        }
    }
}
