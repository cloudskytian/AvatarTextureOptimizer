using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Burst jobs: raster 4px cells, metrics. / Burst 任务：4px 光栅与质量指标。
    /// </summary>
    [BurstCompile]
    public struct AtoRasterTrisJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> UvA;
        [ReadOnly] public NativeArray<float2> UvB;
        [ReadOnly] public NativeArray<float2> UvC;
        public int CellsW;
        public int CellsH;
        public int TexW;
        public int TexH;
        public int CellPx;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Cells;

        public void Execute(int index)
        {
            var a = UvA[index]; var b = UvB[index]; var c = UvC[index];
            var minx = (int)math.floor(math.min(a.x, math.min(b.x, c.x)) * TexW / CellPx);
            var miny = (int)math.floor(math.min(a.y, math.min(b.y, c.y)) * TexH / CellPx);
            var maxx = (int)math.ceil(math.max(a.x, math.max(b.x, c.x)) * TexW / CellPx);
            var maxy = (int)math.ceil(math.max(a.y, math.max(b.y, c.y)) * TexH / CellPx);
            minx = math.clamp(minx, 0, CellsW - 1);
            miny = math.clamp(miny, 0, CellsH - 1);
            maxx = math.clamp(maxx, 0, CellsW - 1);
            maxy = math.clamp(maxy, 0, CellsH - 1);
            for (var y = miny; y <= maxy; y++)
            for (var x = minx; x <= maxx; x++)
            {
                var u = (x + 0.5f) * CellPx / TexW;
                var v = (y + 0.5f) * CellPx / TexH;
                if (PointInTri(new float2(u, v), a, b, c) ||
                    EdgeHitsCell(a, b, x, y) || EdgeHitsCell(b, c, x, y) || EdgeHitsCell(c, a, x, y))
                {
                    Cells[y * CellsW + x] = 1;
                }
            }
        }

        private bool EdgeHitsCell(float2 p, float2 q, int cx, int cy)
        {
            // Conservative: bbox overlap already handled; sample 2 extra points.
            // 保守：包围盒已覆盖，再采样两点。
            var m = (p + q) * 0.5f;
            var x = (int)math.floor(m.x * TexW / CellPx);
            var y = (int)math.floor(m.y * TexH / CellPx);
            return x == cx && y == cy;
        }

        private static bool PointInTri(float2 p, float2 a, float2 b, float2 c)
        {
            var v0 = c - a; var v1 = b - a; var v2 = p - a;
            var dot00 = math.dot(v0, v0);
            var dot01 = math.dot(v0, v1);
            var dot02 = math.dot(v0, v2);
            var dot11 = math.dot(v1, v1);
            var dot12 = math.dot(v1, v2);
            var inv = dot00 * dot11 - dot01 * dot01;
            if (math.abs(inv) < 1e-12f) return false;
            var u = (dot11 * dot02 - dot01 * dot12) / inv;
            var v = (dot00 * dot12 - dot01 * dot02) / inv;
            return u >= -1e-4f && v >= -1e-4f && u + v <= 1f + 1e-4f;
        }
    }

    [BurstCompile]
    public struct AtoSsimDeJob : IJob
    {
        [ReadOnly] public NativeArray<float4> Orig; // linear RGBA
        [ReadOnly] public NativeArray<float4> Cmp;
        public int W;
        public int H;
        public int Kind; // 0 albedo, 1 normal, 2 gray, 3 alpha-blend, 4 cutout
        public float Cutoff;
        public int GrayMask; // bits
        public NativeArray<float> Out; // [0]=msSsim or ssim, [1]=dE, [2]=alpha, [3]=angMean, [4]=angP95

        public void Execute()
        {
            var n = W * H;
            if (n <= 0) { Out[0] = 1; Out[1] = 0; Out[2] = 0; Out[3] = 0; Out[4] = 0; return; }

            if (Kind == 1)
            {
                NormalAngles(n);
                return;
            }
            if (Kind == 2)
            {
                GrayRmse(n);
                return;
            }

            // SSIM on luma + CIEDE2000 mean of sampled pixels.
            double meanO = 0, meanC = 0;
            for (var i = 0; i < n; i++)
            {
                meanO += Luma(Orig[i]);
                meanC += Luma(Cmp[i]);
            }
            meanO /= n; meanC /= n;
            double varO = 0, varC = 0, cov = 0;
            for (var i = 0; i < n; i++)
            {
                var lo = Luma(Orig[i]) - meanO;
                var lc = Luma(Cmp[i]) - meanC;
                varO += lo * lo; varC += lc * lc; cov += lo * lc;
            }
            varO /= n; varC /= n; cov /= n;
            const double c1 = 0.01 * 0.01;
            const double c2 = 0.03 * 0.03;
            var ssim = (2 * meanO * meanC + c1) * (2 * cov + c2) /
                       ((meanO * meanO + meanC * meanC + c1) * (varO + varC + c2) + 1e-12);
            Out[0] = (float)ssim;

            double deSum = 0;
            var step = math.max(1, n / 16384);
            var cnt = 0;
            for (var i = 0; i < n; i += step)
            {
                deSum += Ciede2000(Orig[i], Cmp[i]);
                cnt++;
            }
            Out[1] = (float)(deSum / math.max(1, cnt));

            if (Kind == 3)
            {
                double e = 0;
                for (var i = 0; i < n; i++)
                {
                    var d = Orig[i].w - Cmp[i].w;
                    e += d * d;
                }
                Out[2] = (float)Math.Sqrt(e / n);
            }
            else if (Kind == 4)
            {
                var inter = 0; var uni = 0;
                for (var i = 0; i < n; i++)
                {
                    var a = Orig[i].w >= Cutoff;
                    var b = Cmp[i].w >= Cutoff;
                    if (a && b) inter++;
                    if (a || b) uni++;
                }
                Out[2] = uni == 0 ? 1f : (float)inter / uni;
            }
            else Out[2] = 0;
            Out[3] = 0; Out[4] = 0;
        }

        private void NormalAngles(int n)
        {
            var step = math.max(1, n / 32768);
            var count = 0;
            double sum = 0;
            var angles = new NativeList<float>(n / step + 1, Allocator.Temp);
            for (var i = 0; i < n; i += step)
            {
                var no = DecodeNormal(Orig[i]);
                var nc = DecodeNormal(Cmp[i]);
                var dot = math.clamp(math.dot(no, nc), -1f, 1f);
                var ang = math.degrees(math.acos(dot));
                sum += ang;
                angles.Add(ang);
                count++;
            }
            Out[3] = (float)(sum / math.max(1, count));
            // p95
            var arr = angles.AsArray();
            // partial selection
            var k = (int)(0.95f * (arr.Length - 1));
            k = math.clamp(k, 0, arr.Length - 1);
            // simple insertion on copy is too slow; nth-element-ish bubble of k
            for (var i = 0; i <= k; i++)
            {
                var min = i;
                for (var j = i + 1; j < arr.Length; j++)
                    if (arr[j] < arr[min]) min = j;
                var tmp = arr[i]; arr[i] = arr[min]; arr[min] = tmp;
            }
            Out[4] = arr.Length == 0 ? 0 : arr[k];
            Out[0] = 1; Out[1] = 0; Out[2] = 0;
            angles.Dispose();
        }

        private void GrayRmse(int n)
        {
            double worst = 0;
            for (var ch = 0; ch < 4; ch++)
            {
                if ((GrayMask & (1 << ch)) == 0) continue;
                double e = 0;
                for (var i = 0; i < n; i++)
                {
                    var o = ch == 0 ? Orig[i].x : ch == 1 ? Orig[i].y : ch == 2 ? Orig[i].z : Orig[i].w;
                    var c = ch == 0 ? Cmp[i].x : ch == 1 ? Cmp[i].y : ch == 2 ? Cmp[i].z : Cmp[i].w;
                    var d = o - c;
                    e += d * d;
                }
                worst = math.max(worst, math.sqrt(e / n));
            }
            Out[0] = 1; Out[1] = 0; Out[2] = (float)worst; Out[3] = 0; Out[4] = 0;
        }

        private static float Luma(float4 c) => 0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z;

        private static float3 DecodeNormal(float4 c)
        {
            var n = new float3(c.x * 2f - 1f, c.y * 2f - 1f, c.z * 2f - 1f);
            var len = math.length(n);
            if (len < 1e-8f) return new float3(0, 0, 1);
            return n / len;
        }

        // CIEDE2000 (Sharma et al.). Colors in linear RGB 0-1 → sRGB → Lab.
        // 输入为线性 RGB 0-1，先转到 sRGB 再进 Lab。
        private static double Ciede2000(float4 a, float4 b)
        {
            RgbToLab(LinToSrgb(a.x), LinToSrgb(a.y), LinToSrgb(a.z), out var L1, out var a1, out var b1);
            RgbToLab(LinToSrgb(b.x), LinToSrgb(b.y), LinToSrgb(b.z), out var L2, out var a2, out var b2);
            var kL = 1.0; var kC = 1.0; var kH = 1.0;
            var C1 = Math.Sqrt(a1 * a1 + b1 * b1);
            var C2 = Math.Sqrt(a2 * a2 + b2 * b2);
            var Cab = (C1 + C2) / 2.0;
            var G = 0.5 * (1.0 - Math.Sqrt(Math.Pow(Cab, 7) / (Math.Pow(Cab, 7) + Math.Pow(25.0, 7))));
            var a1p = (1 + G) * a1; var a2p = (1 + G) * a2;
            var C1p = Math.Sqrt(a1p * a1p + b1 * b1);
            var C2p = Math.Sqrt(a2p * a2p + b2 * b2);
            var h1p = Atan2Deg(b1, a1p);
            var h2p = Atan2Deg(b2, a2p);
            var dLp = L2 - L1;
            var dCp = C2p - C1p;
            var dhp = 0.0;
            if (C1p * C2p != 0)
            {
                var dh = h2p - h1p;
                if (dh > 180) dh -= 360;
                if (dh < -180) dh += 360;
                dhp = dh;
            }
            var dHp = 2 * Math.Sqrt(C1p * C2p) * Math.Sin(dhp / 2.0 * Math.PI / 180.0);
            var Lp = (L1 + L2) / 2.0;
            var Cp = (C1p + C2p) / 2.0;
            var hp = h1p + h2p;
            if (C1p * C2p != 0)
            {
                var dh = Math.Abs(h1p - h2p);
                if (dh > 180) hp = (h1p + h2p + 360) / 2.0;
                else hp = (h1p + h2p) / 2.0;
            }
            var T = 1 - 0.17 * Math.Cos((hp - 30) * Math.PI / 180.0) + 0.24 * Math.Cos((2 * hp) * Math.PI / 180.0) +
                    0.32 * Math.Cos((3 * hp + 6) * Math.PI / 180.0) - 0.20 * Math.Cos((4 * hp - 63) * Math.PI / 180.0);
            var dTh = 30 * Math.Exp(-Math.Pow((hp - 275) / 25.0, 2));
            var Rc = 2 * Math.Sqrt(Math.Pow(Cp, 7) / (Math.Pow(Cp, 7) + Math.Pow(25.0, 7)));
            var Sl = 1 + (0.015 * Math.Pow(Lp - 50, 2)) / Math.Sqrt(20 + Math.Pow(Lp - 50, 2));
            var Sc = 1 + 0.045 * Cp;
            var Sh = 1 + 0.015 * Cp * T;
            var Rt = -Math.Sin(2 * dTh * Math.PI / 180.0) * Rc;
            var dL = dLp / (kL * Sl);
            var dC = dCp / (kC * Sc);
            var dH = dHp / (kH * Sh);
            return Math.Sqrt(dL * dL + dC * dC + dH * dH + Rt * dC * dH);
        }

        private static double LinToSrgb(float c)
        {
            if (c <= 0.0031308f) return 12.92f * c;
            return 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
        }

        private static void RgbToLab(double r, double g, double b, out double L, out double A, out double B)
        {
            r = PivotXyz(r); g = PivotXyz(g); b = PivotXyz(b);
            var x = r * 0.4124 + g * 0.3576 + b * 0.1805;
            var y = r * 0.2126 + g * 0.7152 + b * 0.0722;
            var z = r * 0.0193 + g * 0.1192 + b * 0.9505;
            x /= 0.95047; z /= 1.08883;
            x = FLab(x); y = FLab(y); z = FLab(z);
            L = 116 * y - 16;
            A = 500 * (x - y);
            B = 200 * (y - z);
        }

        private static double PivotXyz(double c)
        {
            // c is already sRGB 0-1; convert to linear-ish for XYZ
            if (c <= 0.04045) return c / 12.92;
            return Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        private static double FLab(double t)
        {
            const double e = 216.0 / 24389.0;
            const double k = 24389.0 / 27.0;
            return t > e ? Math.Pow(t, 1.0 / 3.0) : (k * t + 16.0) / 116.0;
        }

        private static double Atan2Deg(double y, double x)
        {
            var a = math.degrees(math.atan2((float)y, (float)x));
            if (a < 0) a += 360;
            return a;
        }
    }
}
