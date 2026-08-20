using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Cheap CPU pull-push bleed. GPU path can replace later.
    /// 廉价 CPU pull-push 渗色；透明贴图 alpha 保持 0。
    /// </summary>
    public static class PullPush
    {
        public static void Fill(Texture2D tex, bool keepAlphaZero)
        {
            var px = tex.GetPixels();
            int w = tex.width, h = tex.height;
            var filled = new bool[px.Length];
            for (int i = 0; i < px.Length; i++)
                filled[i] = px[i].a > 0.001f || px[i].r + px[i].g + px[i].b > 0.001f;

            bool any = true;
            int guard = 64;
            while (any && guard-- > 0)
            {
                any = false;
                var next = (Color[])px.Clone();
                var nf = (bool[])filled.Clone();
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (filled[i]) continue;
                    Color acc = default;
                    int c = 0;
                    Acc(x - 1, y); Acc(x + 1, y); Acc(x, y - 1); Acc(x, y + 1);
                    if (c == 0) continue;
                    acc /= c;
                    if (keepAlphaZero) acc.a = 0;
                    next[i] = acc;
                    nf[i] = true;
                    any = true;
                    continue;
                    void Acc(int xx, int yy)
                    {
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) return;
                        int j = yy * w + xx;
                        if (!filled[j]) return;
                        acc += px[j];
                        c++;
                    }
                }
                px = next;
                filled = nf;
            }
            tex.SetPixels(px);
        }
    }
}
