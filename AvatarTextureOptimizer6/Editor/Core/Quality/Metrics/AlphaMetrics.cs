using System;
using System.Threading.Tasks;

namespace NetFosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// alpha 指标：
    /// - Cutout：对每个引用材质的 Cutoff 做 clip 后轮廓 IoU
    /// - Blend：alpha 线性 RMSE
    /// </summary>
    public static class AlphaMetrics
    {
        /// <summary>clip 后轮廓 IoU。</summary>
        public static float CutoutIoU(float[] a, float[] b, float cutoff)
        {
            if (a.Length != b.Length) return 0f;
            int n = a.Length / 4;
            int inter = 0, union_ = 0;
            for (int i = 0; i < n; i++)
            {
                bool ka = a[i * 4 + 3] >= cutoff;
                bool kb = b[i * 4 + 3] >= cutoff;
                if (ka && kb) inter++;
                if (ka || kb) union_++;
            }
            return union_ == 0 ? 1f : (float)inter / union_;
        }

        /// <summary>alpha 线性 RMSE。</summary>
        public static float BlendRmse(float[] a, float[] b)
        {
            if (a.Length != b.Length) return float.MaxValue;
            int n = a.Length / 4;
            if (n == 0) return 0f;
            double sumSq = 0;
            for (int i = 0; i < n; i++)
            {
                double d = a[i * 4 + 3] - b[i * 4 + 3];
                sumSq += d * d;
            }
            return (float)Math.Sqrt(sumSq / n);
        }
    }
}
