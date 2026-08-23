using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Fosa.AvatarTextureOptimizer.Editor.Quality
{
    internal struct MetricPartial
    {
        public long Count;
        public double SumX, SumY, SumXX, SumYY, SumXY;
        public double DeltaESum;
        public double AlphaSquaredError;
        public double NormalAngleSum;
        public float4 ChannelSquaredError;
        public float4 Minimum;
        public float4 Maximum;
        public long CutoutIntersection;
        public long CutoutUnion;
    }

    /// <summary>EN: Burst-parallel masked perceptual metric reduction. ZH: Burst 并行的遮罩感知指标归约。</summary>
    [BurstCompile(FloatMode.Strict, FloatPrecision.High)]
    internal struct MetricChunkJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float4> Original;
        [ReadOnly] public NativeArray<float4> Candidate;
        [ReadOnly] public NativeArray<byte> Mask;
        [WriteOnly] public NativeArray<MetricPartial> Partials;
        [WriteOnly] public NativeArray<float> NormalAngles;
        public int ChunkSize;
        public int DataWidth;
        public int DataRowOffset;
        public int Semantic;
        public int UsedChannelMask;
        public float Cutoff;
        public int EvaluateCutout;

        public void Execute(int chunk)
        {
            var start = chunk * ChunkSize;
            var end = math.min(start + ChunkSize, Mask.Length);
            var part = new MetricPartial
            {
                Minimum = new float4(float.PositiveInfinity),
                Maximum = new float4(float.NegativeInfinity),
            };
            for (var index = start; index < end; index++)
            {
                NormalAngles[index] = -1f;
                if (Mask[index] == 0) continue;
                var dataIndex = index + DataRowOffset * DataWidth;
                var a = Original[dataIndex];
                var b = Candidate[dataIndex];
                part.Count++;
                part.Minimum = math.min(part.Minimum, a);
                part.Maximum = math.max(part.Maximum, a);

                var lumaA = math.dot(a.xyz, new float3(0.2126f, 0.7152f, 0.0722f));
                var lumaB = math.dot(b.xyz, new float3(0.2126f, 0.7152f, 0.0722f));
                part.SumX += lumaA;
                part.SumY += lumaB;
                part.SumXX += lumaA * lumaA;
                part.SumYY += lumaB * lumaB;
                part.SumXY += lumaA * lumaB;
                var alphaError = a.w - b.w;
                part.AlphaSquaredError += alphaError * alphaError;
                var difference = a - b;
                part.ChannelSquaredError += difference * difference;

                if (Semantic == 0 || Semantic == 1) part.DeltaESum += DeltaE2000(RgbToLab(a.xyz), RgbToLab(b.xyz));
                if (Semantic == 2)
                {
                    var na = math.normalizesafe(a.xyz * 2f - 1f, new float3(0f, 0f, 1f));
                    var nb = math.normalizesafe(b.xyz * 2f - 1f, new float3(0f, 0f, 1f));
                    var angle = math.degrees(math.acos(math.clamp(math.dot(na, nb), -1f, 1f)));
                    part.NormalAngleSum += angle;
                    NormalAngles[index] = angle;
                }
                if (EvaluateCutout != 0)
                {
                    var aa = a.w >= Cutoff;
                    var bb = b.w >= Cutoff;
                    if (aa && bb) part.CutoutIntersection++;
                    if (aa || bb) part.CutoutUnion++;
                }
            }
            Partials[chunk] = part;
        }

        private static float3 RgbToLab(float3 rgb)
        {
            var xyz = new float3(
                math.dot(rgb, new float3(0.4124564f, 0.3575761f, 0.1804375f)),
                math.dot(rgb, new float3(0.2126729f, 0.7151522f, 0.0721750f)),
                math.dot(rgb, new float3(0.0193339f, 0.1191920f, 0.9503041f)));
            xyz /= new float3(0.95047f, 1.00000f, 1.08883f);
            xyz = new float3(LabF(xyz.x), LabF(xyz.y), LabF(xyz.z));
            return new float3(116f * xyz.y - 16f, 500f * (xyz.x - xyz.y), 200f * (xyz.y - xyz.z));
        }

        private static float LabF(float value)
        {
            const float epsilon = 216f / 24389f;
            const float kappa = 24389f / 27f;
            return value > epsilon ? math.pow(value, 1f / 3f) : (kappa * value + 16f) / 116f;
        }

        // EN: Sharma et al. CIEDE2000 reference formula. ZH: Sharma 等人的 CIEDE2000 参考公式。
        private static float DeltaE2000(float3 lab1, float3 lab2)
        {
            var c1 = math.length(lab1.yz); var c2 = math.length(lab2.yz); var cBar = (c1 + c2) * 0.5f;
            var cBar7 = math.pow(cBar, 7f);
            var g = 0.5f * (1f - math.sqrt(cBar7 / (cBar7 + math.pow(25f, 7f))));
            var a1p = (1f + g) * lab1.y; var a2p = (1f + g) * lab2.y;
            var c1p = math.sqrt(a1p * a1p + lab1.z * lab1.z); var c2p = math.sqrt(a2p * a2p + lab2.z * lab2.z);
            var h1p = Hue(a1p, lab1.z); var h2p = Hue(a2p, lab2.z);
            var dL = lab2.x - lab1.x; var dC = c2p - c1p;
            var dh = h2p - h1p;
            if (c1p * c2p == 0f) dh = 0f;
            else if (dh > 180f) dh -= 360f;
            else if (dh < -180f) dh += 360f;
            var dH = 2f * math.sqrt(c1p * c2p) * math.sin(math.radians(dh * 0.5f));
            var lBar = (lab1.x + lab2.x) * 0.5f; var cpBar = (c1p + c2p) * 0.5f;
            float hpBar;
            if (c1p * c2p == 0f) hpBar = h1p + h2p;
            else if (math.abs(h1p - h2p) <= 180f) hpBar = (h1p + h2p) * 0.5f;
            else hpBar = (h1p + h2p + (h1p + h2p < 360f ? 360f : -360f)) * 0.5f;
            var t = 1f - 0.17f * math.cos(math.radians(hpBar - 30f)) + 0.24f * math.cos(math.radians(2f * hpBar)) +
                    0.32f * math.cos(math.radians(3f * hpBar + 6f)) - 0.20f * math.cos(math.radians(4f * hpBar - 63f));
            var dTheta = 30f * math.exp(-math.pow((hpBar - 275f) / 25f, 2f));
            var cp7 = math.pow(cpBar, 7f); var rc = 2f * math.sqrt(cp7 / (cp7 + math.pow(25f, 7f)));
            var sl = 1f + 0.015f * math.pow(lBar - 50f, 2f) / math.sqrt(20f + math.pow(lBar - 50f, 2f));
            var sc = 1f + 0.045f * cpBar; var sh = 1f + 0.015f * cpBar * t;
            var rt = -math.sin(math.radians(2f * dTheta)) * rc;
            var x = dL / sl; var y = dC / sc; var z = dH / sh;
            return math.sqrt(math.max(0f, x * x + y * y + z * z + rt * y * z));
        }

        private static float Hue(float a, float b)
        {
            var degrees = math.degrees(math.atan2(b, a));
            return degrees < 0f ? degrees + 360f : degrees;
        }
    }

    /// <summary>EN: Burst raster mask for actual triangle coverage. ZH: 针对三角形实际覆盖区的 Burst 光栅遮罩。</summary>
    [BurstCompile(FloatMode.Fast, FloatPrecision.Standard)]
    internal struct IslandMaskJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> TrianglePoints;
        [WriteOnly] public NativeArray<byte> Mask;
        public int Width;
        public int Height;
        public int YOffset;
        public int FullHeight;

        public void Execute(int index)
        {
            var x = index % Width;
            var y = index / Width + YOffset;
            var point = new float2((x + 0.5f) / Width, (y + 0.5f) / FullHeight);
            byte covered = 0;
            for (var t = 0; t + 2 < TrianglePoints.Length; t += 3)
            {
                if (Inside(point, TrianglePoints[t], TrianglePoints[t + 1], TrianglePoints[t + 2])) { covered = 1; break; }
            }
            Mask[index] = covered;
        }

        private static bool Inside(float2 p, float2 a, float2 b, float2 c)
        {
            var d1 = Cross(p - b, a - b); var d2 = Cross(p - c, b - c); var d3 = Cross(p - a, c - a);
            var hasNegative = d1 < -1e-6f || d2 < -1e-6f || d3 < -1e-6f;
            var hasPositive = d1 > 1e-6f || d2 > 1e-6f || d3 > 1e-6f;
            return !(hasNegative && hasPositive);
        }
        private static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;
    }
}
