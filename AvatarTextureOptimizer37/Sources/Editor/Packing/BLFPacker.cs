// ============================================================================
// ATO - BLF packer with 4px raster masks
// ATO - 4px 光栅掩码 BLF 装箱器
//
// Full-scan bottom-left-first  全扫描底左优先：
//   for rot in {0, 90}:
//     for y from bottom to top:
//       for x from left to right:
//         if mask fits in occupancy grid: place
//  旋转 0/90 依次尝试；自下而上、自左而右全扫描；掩码与占用网格（4px 单元）
//  无交叠即放置。
// ============================================================================

#region

using System.Collections.Generic;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Packing
{
    public static class BLFPacker
    {
        public struct Placement
        {
            public int X, Y, Rot90;
        }

        /// <summary>Tries to place the item's raster into a WxH page with the
        /// given occupancy cells (4px grid). Returns the placement or null.
        /// 尝试将条目光栅放入 WxH 页（4px 占用网格）。成功返回摆放，否则 null。</summary>
        public static Placement? TryPlace(
            int pageW, int pageH,
            bool[] occupied, int occW, int occH,
            System.Numerics.BigInteger mask, int maskW, int maskH, int cellCount,
            out System.Numerics.BigInteger placedMask)
        {
            placedMask = System.Numerics.BigInteger.Zero;
            int mw = maskW / 4, mh = maskH / 4;
            int pw = pageW / 4, ph = pageH / 4;
            if (mw > pw || mh > ph) return null;

            // extract cell list  提取单元列表
            var cells = new List<(int cx, int cy)>();
            for (int cy = 0; cy < mh; cy++)
            {
                for (int cx = 0; cx < mw; cx++)
                {
                    if ((mask >> (cy * mw + cx)) != 0) cells.Add((cx, cy));
                }
            }
            if (cells.Count == 0) cells.Add((0, 0));

            var rotCells = new List<(int cx, int cy)>(cells);
            int rMw = mw, rMh = mh;

            // rotation 0  旋转 0
            if (ScanAndPlace(pw, ph, mw, mh, cells, occupied))
            {
                placedMask = ToMask(cells, mw, mh);
                return new Placement { X = LastX * 4, Y = LastY * 4, Rot90 = 0 };
            }

            // rotation 90 (transpose)  旋转 90（转置）
            if (mw != mh)
            {
                rotCells.Clear();
                foreach (var (cx, cy) in cells) rotCells.Add((cy, mw - 1 - cx));
                (rMw, rMh) = (mh, mw);
                if (rMw <= pw && rMh <= ph)
                {
                    if (ScanAndPlace(pw, ph, rMw, rMh, rotCells, occupied))
                    {
                        placedMask = ToMask(rotCells, rMw, rMh);
                        return new Placement { X = LastX * 4, Y = LastY * 4, Rot90 = 1 };
                    }
                }
            }
            else
            {
                rotCells.Clear();
                foreach (var (cx, cy) in cells) rotCells.Add((cy, mw - 1 - cx));
                if (ScanAndPlace(pw, ph, rMw, rMh, rotCells, occupied))
                {
                    placedMask = ToMask(rotCells, rMw, rMh);
                    return new Placement { X = LastX * 4, Y = LastY * 4, Rot90 = 1 };
                }
            }
            return null;
        }

        private static int LastX, LastY;

        private static bool ScanAndPlace(int pw, int ph, int mw, int mh,
            List<(int cx, int cy)> cells, bool[] occupied)
        {
            // bottom-left first: y from bottom  底左优先：y 自底部
            for (int oy = ph - mh; oy >= 0; oy--)
            {
                for (int ox = 0; ox <= pw - mw; ox++)
                {
                    bool fits = true;
                    foreach (var (cx, cy) in cells)
                    {
                        if (occupied[(oy + cy) * pw + (ox + cx)])
                        {
                            fits = false;
                            break;
                        }
                    }
                    if (fits)
                    {
                        foreach (var (cx, cy) in cells)
                        {
                            occupied[(oy + cy) * pw + (ox + cx)] = true;
                        }
                        LastX = ox;
                        LastY = oy;
                        return true;
                    }
                }
            }
            return false;
        }

        private static System.Numerics.BigInteger ToMask(List<(int cx, int cy)> cells, int mw, int mh)
        {
            var m = System.Numerics.BigInteger.Zero;
            foreach (var (cx, cy) in cells)
            {
                m |= System.Numerics.BigInteger.One << (cy * mw + cx);
            }
            return m;
        }
    }
}
