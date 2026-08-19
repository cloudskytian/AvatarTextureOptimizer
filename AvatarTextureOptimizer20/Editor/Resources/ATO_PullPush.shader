Shader "Hidden/ATO/PullPush"
{
    // Pull-push (infinite) edge dilation. Pull pass: downsample averaging only valid texels
    // (validity in alpha of working buffer). Push pass: fill invalid texels from coarser level.
    // GPU pull-push 无限外扩：Pull 只平均有效像素，Push 用粗层填充无效像素。
    Properties { _MainTex ("Texture", 2D) = "white" {} _CoarseTex ("Coarse", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass // 0: pull (downsample valid-weighted)
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 frag(v2f_img i) : SV_Target
            {
                float2 ts = _MainTex_TexelSize.xy;
                float4 acc = 0;
                // sample the 4 children texels / 采样4个子像素
                float2 o[4] = { float2(-0.25, -0.25), float2(0.25, -0.25), float2(-0.25, 0.25), float2(0.25, 0.25) };
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float4 c = tex2D(_MainTex, i.uv + o[k] * ts * 2.0);
                    acc += float4(c.rgb * c.a, c.a); // alpha stores validity weight / alpha 即有效权重
                }
                float3 rgb = acc.a > 1e-6 ? acc.rgb / acc.a : 0;
                return float4(rgb, saturate(acc.a));
            }
            ENDCG
        }

        Pass // 1: push (fill invalid from coarse)
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;   // fine level / 细层
            sampler2D _CoarseTex; // filled coarse level / 已填充粗层
            float4 frag(v2f_img i) : SV_Target
            {
                float4 fine = tex2D(_MainTex, i.uv);
                float4 coarse = tex2D(_CoarseTex, i.uv);
                float3 rgb = lerp(coarse.rgb, fine.rgb, saturate(fine.a));
                return float4(rgb, max(fine.a, coarse.a));
            }
            ENDCG
        }

        Pass // 2: final combine: filled rgb, ORIGINAL alpha (0 outside islands)
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;   // original composited atlas / 原始合成图集
            sampler2D _CoarseTex; // fully filled / 已完全填充
            float4 frag(v2f_img i) : SV_Target
            {
                float4 fine = tex2D(_MainTex, i.uv);
                float4 coarse = tex2D(_CoarseTex, i.uv);
                float3 rgb = lerp(coarse.rgb, fine.rgb, saturate(fine.a));
                return float4(rgb, fine.a); // keep alpha 0 in empty space / 空白区alpha保持0
            }
            ENDCG
        }
    }
    Fallback Off
}
