using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Infinite pull-push / dilate fill of empty atlas texels.
    /// Transparent atlases keep alpha = 0 in the empty region (known colour bleed, good enough).
    /// 空白图集纹素的无限外扩填充。透明图集空白处 Alpha 保持 0（渗色已知，够用）。
    /// </summary>
    public static class PullPushBleed
    {
        public static void Fill(Color[] px, int w, int h, bool keepAlphaZero)
        {
            if (px == null || w <= 0 || h <= 0) return;
            var filled = new bool[px.Length];
            var any = false;
            for (int i = 0; i < px.Length; i++)
            {
                // Consider a texel "content" if it was written (alpha>0 or rgb non-zero after blit).
                // 写入过的纹素视为内容。
                if (px[i].a > 0.001f || px[i].r + px[i].g + px[i].b > 0.001f)
                {
                    filled[i] = true;
                    any = true;
                }
            }

            if (!any) return;

            var next = new bool[px.Length];
            var changed = true;
            var guard = w + h + 8;
            while (changed && guard-- > 0)
            {
                changed = false;
                System.Array.Copy(filled, next, filled.Length);
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    if (filled[i]) continue;
                    float r = 0, g = 0, b = 0, a = 0;
                    int n = 0;
                    Acc(x - 1, y);
                    Acc(x + 1, y);
                    Acc(x, y - 1);
                    Acc(x, y + 1);
                    if (n == 0) continue;
                    var c = new Color(r / n, g / n, b / n, keepAlphaZero ? 0f : a / n);
                    px[i] = c;
                    next[i] = true;
                    changed = true;

                    void Acc(int xx, int yy)
                    {
                        if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) return;
                        var j = yy * w + xx;
                        if (!filled[j]) return;
                        r += px[j].r;
                        g += px[j].g;
                        b += px[j].b;
                        a += px[j].a;
                        n++;
                    }
                }

                var tmp = filled;
                filled = next;
                next = tmp;
            }
        }
    }
}
