// BakingJobs.cs
// Burst jobs used during atlas baking: pull-push bleed (infinite edge extension with
// alpha preserved as 0 for transparent atlases) and atlas finalization helpers.
// 图集烘焙用 Burst 作业:pull-push 渗色(无限外扩;透明图集空白区 alpha 保持 0)。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace net.fosa.ato
{
    /// <summary>One pull-push pyramid level computation. / 计算 pull-push 金字塔一层(下采样)。</summary>
    [BurstCompile]
    internal struct PullPushDownJob : IJob
    {
        [ReadOnly] public NativeArray<Color32> SrcColor; // premultiplied colors / 预乘颜色
        [ReadOnly] public NativeArray<float> SrcWeight;  // validity / 有效度
        public int SrcW, SrcH;
        [WriteOnly] public NativeArray<Color32> DstColor;
        [WriteOnly] public NativeArray<float> DstWeight;
        public int DstW, DstH;

        public void Execute()
        {
            for (int y = 0; y < DstH; y++)
            {
                for (int x = 0; x < DstW; x++)
                {
                    float4 sum = 0; float wsum = 0;
                    for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = math.min(x * 2 + dx, SrcW - 1), sy = math.min(y * 2 + dy, SrcH - 1);
                        int si = sy * SrcW + sx;
                        float w = SrcWeight[si];
                        if (w <= 0) continue;
                        var c = SrcColor[si];
                        sum += new float4(c.r, c.g, c.b, c.a) * w;
                        wsum += w;
                    }
                    if (wsum <= 0)
                    {
                        DstColor[y * DstW + x] = new Color32();
                        DstWeight[y * DstW + x] = 0f;
                    }
                    else
                    {
                        float4 v = sum / wsum;
                        DstColor[y * DstW + x] = new Color32(
                            (byte)math.clamp(v.x + 0.5f, 0, 255),
                            (byte)math.clamp(v.y + 0.5f, 0, 255),
                            (byte)math.clamp(v.z + 0.5f, 0, 255),
                            (byte)math.clamp(v.w + 0.5f, 0, 255));
                        DstWeight[y * DstW + x] = wsum * 0.25f;
                    }
                }
            }
        }
    }

    /// <summary>Push pass: fill invalid dst pixels from the coarser level. / 上推:用粗层填充无效像素。</summary>
    [BurstCompile]
    internal struct PullPushUpJob : IJob
    {
        public NativeArray<Color32> FineColor;   // read-write / 读写
        public NativeArray<float> FineWeight;
        public int FineW, FineH;
        [ReadOnly] public NativeArray<Color32> CoarseColor;
        [ReadOnly] public NativeArray<float> CoarseWeight;
        public int CoarseW, CoarseH;

        public void Execute()
        {
            for (int y = 0; y < FineH; y++)
            {
                for (int x = 0; x < FineW; x++)
                {
                    int i = y * FineW + x;
                    if (FineWeight[i] > 0) continue; // already valid / 已有效
                    // bilateral-ish: bilinear from coarse / 从粗层双线性
                    float gx = (x - 0.5f) * 0.5f, gy = (y - 0.5f) * 0.5f;
                    int x0 = math.clamp((int)math.floor(gx), 0, CoarseW - 1);
                    int y0 = math.clamp((int)math.floor(gy), 0, CoarseH - 1);
                    int x1 = math.min(x0 + 1, CoarseW - 1), y1 = math.min(y0 + 1, CoarseH - 1);
                    float tx = math.saturate(gx - x0), ty = math.saturate(gy - y0);
                    float4 c00 = ToF4(CoarseColor[y0 * CoarseW + x0]);
                    float4 c01 = ToF4(CoarseColor[y0 * CoarseW + x1]);
                    float4 c10 = ToF4(CoarseColor[y1 * CoarseW + x0]);
                    float4 c11 = ToF4(CoarseColor[y1 * CoarseW + x1]);
                    float4 v = math.lerp(math.lerp(c00, c01, tx), math.lerp(c10, c11, tx), ty);
                    FineColor[i] = new Color32(
                        (byte)math.clamp(v.x + 0.5f, 0, 255),
                        (byte)math.clamp(v.y + 0.5f, 0, 255),
                        (byte)math.clamp(v.z + 0.5f, 0, 255),
                        (byte)math.clamp(v.w + 0.5f, 0, 255));
                    FineWeight[i] = 0f; // stays "inferred" / 标记为推断
                }
            }
        }

        private static float4 ToF4(Color32 c) => new float4(c.r, c.g, c.b, c.a);
    }
}
