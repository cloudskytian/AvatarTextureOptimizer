using System;
using System.Threading.Tasks;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// 法线贴图指标：解码 → 重采样后重归一化 → 编码，比较每像素角度误差，取 p95。
    /// 输入为线性空间 RGBA（法线存于 RGB）。
    /// </summary>
    public static class NormalMetrics
    {
        public static float AngleErrorP95(float[] a, float[] b)
        {
            if (a.Length != b.Length) return float.MaxValue;
            int n = a.Length / 4;
            if (n == 0) return 0f;

            var errors = new float[n];
            Parallel.For(0, n, i =>
            {
                int o = i * 4;
                var n1 = ImageOps.DecodeNormal(a[o], a[o + 1], a[o + 2]);
                var n2 = ImageOps.DecodeNormal(b[o], b[o + 1], b[o + 2]);
                float dot = Mathf.Clamp(Vector3.Dot(n1, n2), -1f, 1f);
                errors[i] = Mathf.Rad2Deg * Mathf.Acos(dot);
            });

            Array.Sort(errors);
            int idx = Mathf.Min(n - 1, (int)(n * 0.95f));
            return errors[idx];
        }
    }
}
