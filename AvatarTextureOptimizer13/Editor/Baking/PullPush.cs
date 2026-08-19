// ATO — Avatar Texture Optimizer
// Pull-push edge dilation: fills the empty regions of an atlas with the colors of the
// nearest island edge (infinite dilation). Alpha stays 0 in empty regions for transparent
// atlases. Normal atlases fill with the neutral normal. CPU reference implementation.
// pull-push 边缘外扩：用最近岛边缘颜色填满图集空白区域（无限外扩）。透明图集空白区 alpha 保持 0。
// 法线图集空白区填中性法线。CPU 参考实现。

using UnityEngine;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Pull-push empty-region fill. pull-push 空白填充。
    /// </summary>
    public static class PullPush
    {
        /// <summary>
        /// Fill empty pixels (filled == false) with dilated edge colors.
        /// 用外扩的边缘颜色填充空白像素（filled == false）。
        /// </summary>
        public static void Fill(Color32[] pixels, bool[] filled, int w, int h, ATOTextureKind kind, bool transparent)
        {
            var queue = new System.Collections.Generic.Queue<int>();
            var processed = new bool[w * h];

            // Seed the frontier with filled pixels adjacent to empty ones. 把与空白相邻的已填充像素加入前沿。
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                if (!filled[idx]) continue;
                if (HasEmptyNeighbor(filled, x, y, w, h))
                {
                    queue.Enqueue(idx);
                    processed[idx] = true;
                }
            }

            // Breadth-first dilation; each empty pixel is filled once with the average of its
            // already-filled neighbors. 广度优先外扩；每个空白像素用其已填充邻居的平均色填充一次。
            var stack = new System.Collections.Generic.List<int>();
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                int cx = cur % w, cy = cur / w;
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + DX[d], ny = cy + DY[d];
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int nidx = ny * w + nx;
                    if (filled[nidx] || processed[nidx]) continue;
                    pixels[nidx] = AverageFilledNeighbor(pixels, filled, nx, ny, w, h, kind, transparent);
                    processed[nidx] = true;
                    queue.Enqueue(nidx);
                }
            }

            // Any remaining unreachable empty pixels get the neutral fill. 其余不可达空白用中性色填充。
            for (int i = 0; i < pixels.Length; i++)
            {
                if (filled[i] || processed[i]) continue;
                pixels[i] = NeutralFill(kind, transparent);
            }
        }

        private static readonly int[] DX = { 1, -1, 0, 0 };
        private static readonly int[] DY = { 0, 0, 1, -1 };

        private static bool HasEmptyNeighbor(bool[] filled, int x, int y, int w, int h)
        {
            for (int d = 0; d < 4; d++)
            {
                int nx = x + DX[d], ny = y + DY[d];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                if (!filled[ny * w + nx]) return true;
            }
            return false;
        }

        private static Color32 AverageFilledNeighbor(Color32[] pixels, bool[] filled, int x, int y, int w, int h,
            ATOTextureKind kind, bool transparent)
        {
            int r = 0, g = 0, b = 0, a = 0, cnt = 0;
            for (int d = 0; d < 4; d++)
            {
                int nx = x + DX[d], ny = y + DY[d];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int idx = ny * w + nx;
                if (!filled[idx]) continue;
                var c = pixels[idx];
                r += c.r; g += c.g; b += c.b; a += c.a; cnt++;
            }
            if (cnt == 0) return NeutralFill(kind, transparent);
            var avg = new Color32((byte)(r / cnt), (byte)(g / cnt), (byte)(b / cnt), (byte)(a / cnt));
            if (kind == ATOTextureKind.Color && transparent) avg.a = 0; // transparent: keep alpha 0 透明：保持 alpha 0
            return avg;
        }

        private static Color32 NeutralFill(ATOTextureKind kind, bool transparent)
        {
            switch (kind)
            {
                case ATOTextureKind.NormalMap: return new Color32(128, 128, 255, 255);
                default:
                    return transparent ? new Color32(0, 0, 0, 0) : new Color32(0, 0, 0, 255);
            }
        }
    }
}
