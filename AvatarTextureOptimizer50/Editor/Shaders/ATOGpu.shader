// -----------------------------------------------------------------------------
// ATOGpu.shader — GPU passes used by ATO's pull-push bleed:
//   Pass 0: pull  — coverage-weighted 2x2 downsample (premultiplied RGB)
//   Pass 1: push  — upsample from coarser level, keep own covered pixels
// ATOGpu.shader — ATO pull-push 渗色所用的 GPU pass：
//   Pass 0：pull —— 带覆盖率加权的 2x2 降采样（RGB 预乘）
//   Pass 1：push —— 从更粗层上采样，已覆盖像素保持原样
// -----------------------------------------------------------------------------

Shader "Hidden/ATO/Gpu"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // ---------------- Pass 0: pull / 下采样 ----------------
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag_pull
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            float4 frag_pull(v2f_img i) : SV_Target
            {
                // Sum the 2x2 block, premultiplied by coverage (alpha>0.001).
                // 对 2x2 块按覆盖率（alpha>0.001）预乘求和。
                float4 acc = 0;
                float2 baseUV = i.uv - 0.25 * _MainTex_TexelSize.xy;
                [unroll]
                for (int y = 0; y < 2; y++)
                {
                    [unroll]
                    for (int x = 0; x < 2; x++)
                    {
                        float2 uv = baseUV + float2(x, y) * _MainTex_TexelSize.xy;
                        float4 c = tex2D(_MainTex, uv);
                        float w = c.a > 0.001 ? 1.0 : 0.0;
                        acc += float4(c.rgb * w, w);
                    }
                }
                if (acc.a < 0.001) return 0;
                // alpha channel is a "filled" flag, not real alpha
                // alpha 通道作为“已填充”标记，不是真实 alpha
                return float4(acc.rgb / acc.a, 1.0);
            }
            ENDCG
        }

        // ---------------- Pass 1: push / 上采样填充 ----------------
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag_push
            #include "UnityCG.cginc"

            sampler2D _MainTex;   // coarser level (set by Blit) / 较粗层（Blit 设置）
            sampler2D _OwnTex;    // this level's own pull result / 本层自身 pull 结果

            float4 frag_push(v2f_img i) : SV_Target
            {
                float4 own = tex2D(_OwnTex, i.uv);
                if (own.a > 0.5) return own;          // keep covered pixels / 保留已覆盖像素
                float4 up = tex2D(_MainTex, i.uv);    // bilinear parent color / 父层颜色
                return float4(up.rgb, 0.0);           // filled RGB, still "empty" flag / 填充RGB但仍标记为空
            }
            ENDCG
        }
    }
    Fallback Off
}
