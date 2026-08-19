// ATO — Avatar Texture Optimizer
// A compact 1-bit-per-cell grid used by the island rasterizer and BLF packer.
// 供岛光栅化与 BLF 装箱使用的紧凑单比特网格。
//
// Cells correspond to 4px blocks of the atlas (see CLAUDE.md #15). This C# reference is
// structured so a Burst/NativeArray backend can replace the hot loops later.
// 单元对应图集的 4px 块（CLAUDE.md #15）。此 C# 参考实现的结构允许日后用 Burst/NativeArray 后端替换热循环。

namespace net.fosa.ato.editor
{
    /// <summary>
    /// 1-bit grid with 90° rotation (transpose). 支持 90° 旋转（转置）的单比特网格。
    /// </summary>
    public sealed class BitMask
    {
        public readonly int Width;   // cells 单元
        public readonly int Height;
        private readonly byte[] _bits; // row-major, LSB-first. 行主序，低位在前。

        public BitMask(int w, int h)
        {
            Width = w;
            Height = h;
            _bits = new byte[(w * h + 7) >> 3];
        }

        public bool Get(int x, int y)
        {
            int idx = y * Width + x;
            return (_bits[idx >> 3] & (1 << (idx & 7))) != 0;
        }

        public void Set(int x, int y)
        {
            int idx = y * Width + x;
            _bits[idx >> 3] |= (byte)(1 << (idx & 7));
        }

        /// <summary>Rotate 90° clockwise. 顺时针旋转 90°。</summary>
        public BitMask Rotate90CW()
        {
            var r = new BitMask(Height, Width);
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (Get(x, y)) r.Set(Height - 1 - y, x);
            return r;
        }

        /// <summary>Rotate by 90° steps (0..3). 按 90° 步进旋转（0..3）。</summary>
        public BitMask Rotate(int steps)
        {
            steps = ((steps % 4) + 4) % 4;
            var cur = this;
            for (int i = 0; i < steps; i++) cur = cur.Rotate90CW();
            return cur;
        }

        /// <summary>Dilate by n cells in all 4 directions. 四方向膨胀 n 个单元。</summary>
        public BitMask Dilate(int n)
        {
            if (n <= 0) return this;
            var r = new BitMask(Width, Height);
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                if (!Get(x, y)) continue;
                for (int dy = -n; dy <= n; dy++)
                for (int dx = -n; dx <= n; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && nx < Width && ny >= 0 && ny < Height) r.Set(nx, ny);
                }
            }
            return r;
        }

        /// <summary>True when two masks overlap. 两个掩码是否重叠。</summary>
        public static bool Overlaps(BitMask a, BitMask b, int ax, int ay, int bx, int by)
        {
            // Compute the intersection rectangle. 计算相交矩形。
            int x0 = System.Math.Max(ax, bx);
            int y0 = System.Math.Max(ay, by);
            int x1 = System.Math.Min(ax + a.Width, bx + b.Width);
            int y1 = System.Math.Min(ay + a.Height, by + b.Height);
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                if (a.Get(x - ax, y - ay) && b.Get(x - bx, y - by)) return true;
            }
            return false;
        }

        /// <summary>Copy this mask into another at an offset (OR). 在偏移处把本掩码 OR 进另一掩码。</summary>
        public void BlitInto(BitMask dst, int ox, int oy)
        {
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                if (!Get(x, y)) continue;
                int nx = x + ox, ny = y + oy;
                if (nx >= 0 && nx < dst.Width && ny >= 0 && ny < dst.Height) dst.Set(nx, ny);
            }
        }

        /// <summary>Fits inside the given bounds (as top-left offset). 是否可放入给定边界（作为左上偏移）。</summary>
        public bool Fits(int ox, int oy, int w, int h) => ox >= 0 && oy >= 0 && ox + Width <= w && oy + Height <= h;
    }
}
