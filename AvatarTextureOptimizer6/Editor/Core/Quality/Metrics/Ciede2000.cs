using System;
using System.Threading.Tasks;
using NetFosa.AvatarTextureOptimizer.Editor.Utils;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// CIEDE2000 平均色差。输入线性 RGBA 交错数组，计算每像素 sRGB→Lab 后 ΔE2000 的平均值。
    /// </summary>
    public static class Ciede2000
    {
        public static float MeanDeltaE(float[] a, float[] b)
        {
            if (a.Length != b.Length) return float.MaxValue;
            int n = a.Length / 4;
            if (n == 0) return 0f;
            double sum = 0;
            object lockObj = new object();

            Parallel.For(0, n, i =>
            {
                int o = i * 4;
                // a 已为线性，需转回 sRGB 再进 Lab（ΔE 定义在 sRGB 感知空间）
                var lab1 = ColorSpace.SrgbToLab(new Vector3(
                    ColorSpace.LinearToSrgb(a[o]), ColorSpace.LinearToSrgb(a[o + 1]), ColorSpace.LinearToSrgb(a[o + 2])));
                var lab2 = ColorSpace.SrgbToLab(new Vector3(
                    ColorSpace.LinearToSrgb(b[o]), ColorSpace.LinearToSrgb(b[o + 1]), ColorSpace.LinearToSrgb(b[o + 2])));
                double d = ColorSpace.Ciede2000(lab1, lab2);
                lock (lockObj) sum += d;
            });

            return (float)(sum / n);
        }
    }
}
