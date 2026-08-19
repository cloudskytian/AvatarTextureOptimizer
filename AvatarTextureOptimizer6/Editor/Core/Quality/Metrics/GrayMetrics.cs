using System;
using System.Threading.Tasks;

namespace NetFosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// 灰度/蒙版贴图指标：仅在"被使用的通道"上、线性空间 RMSE，逐通道取最差。
    /// 被使用通道 = 该通道存在变化（非恒定）的通道。
    /// </summary>
    public static class GrayMetrics
    {
        public static float WorstChannelRmse(float[] a, float[] b)
        {
            if (a.Length != b.Length) return float.MaxValue;
            int n = a.Length / 4;
            if (n == 0) return 0f;

            bool[] used = new bool[4];
            // 检测被使用通道（参考图 a 上存在变化的通道；alpha 仅在有变化时算）
            int step = Math.Max(1, n / 4096);
            float[] first = { a[0], a[1], a[2], a[3] };
            for (int i = 0; i < n; i += step)
            {
                for (int c = 0; c < 4; c++)
                {
                    if (Math.Abs(a[i * 4 + c] - first[c]) > 1e-4f) used[c] = true;
                }
            }

            double worst = 0;
            object lockObj = new object();
            Parallel.For(0, 4, c =>
            {
                if (!used[c]) return;
                double sumSq = 0;
                for (int i = 0; i < n; i++)
                {
                    double d = a[i * 4 + c] - b[i * 4 + c];
                    sumSq += d * d;
                }
                double rmse = Math.Sqrt(sumSq / n);
                lock (lockObj)
                {
                    if (rmse > worst) worst = rmse;
                }
            });

            return (float)worst;
        }
    }
}
