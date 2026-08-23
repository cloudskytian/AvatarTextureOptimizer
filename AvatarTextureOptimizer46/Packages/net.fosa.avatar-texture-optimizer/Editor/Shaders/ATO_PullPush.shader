// SPDX-License-Identifier: MIT
// EN: Pull-push edge dilation. Pass 0 pulls (downsamples with coverage weighting), pass 1 pushes
//     (upsamples and fills only where coverage is missing). Iterating down to 1x1 and back gives an
//     effectively infinite dilation that fills the whole atlas. Alpha of transparent texels stays 0.
// ZH: Pull-push 边缘外扩。Pass 0 为 pull（按覆盖度加权降采样），Pass 1 为 push
//     （上采样并只填补缺少覆盖的位置）。一路降到 1x1 再升回来即可获得实际上无限的外扩，
//     填满整张图集。透明像素的 alpha 保持为 0。
Shader "Hidden/ATO/PullPush"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _ATO_Coarse ("Coarser level", 2D) = "black" {}
        _ATO_TexelSize ("Source texel size", Vector) = (0,0,0,0)
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // EN: Pass 0 - PULL. Averages the four children weighted by their coverage.
        // ZH: Pass 0 - PULL。按覆盖度对四个子像素加权求平均。
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _ATO_TexelSize;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert (appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }

            float4 frag (v2f i) : SV_Target
            {
                float2 t = _ATO_TexelSize.xy;
                float4 acc = 0;
                float wsum = 0;
                [unroll] for (int dy = 0; dy < 2; dy++)
                {
                    [unroll] for (int dx = 0; dx < 2; dx++)
                    {
                        float2 uv = i.uv + float2((dx - 0.5) * t.x, (dy - 0.5) * t.y);
                        float4 s = tex2D(_MainTex, uv);
                        // EN: The w channel carries coverage, not the texture's alpha.
                        // ZH: w 通道承载覆盖度，而非贴图的 alpha。
                        acc += float4(s.rgb, 0) * s.w;
                        wsum += s.w;
                    }
                }
                if (wsum <= 0) return float4(0,0,0,0);
                return float4(acc.rgb / wsum, saturate(wsum * 0.25 * 4.0));
            }
            ENDCG
        }

        // EN: Pass 1 - PUSH. Keeps covered texels, fills uncovered ones from the coarser level.
        // ZH: Pass 1 - PUSH。保留已覆盖像素，用更粗一级填补未覆盖的位置。
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            sampler2D _ATO_Coarse;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert (appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }

            float4 frag (v2f i) : SV_Target
            {
                float4 fine = tex2D(_MainTex, i.uv);
                if (fine.w >= 0.999) return fine;
                float4 coarse = tex2D(_ATO_Coarse, i.uv);
                float3 rgb = lerp(coarse.rgb, fine.rgb, fine.w);
                float cov = max(fine.w, coarse.w);
                return float4(rgb, cov);
            }
            ENDCG
        }
    }
}
