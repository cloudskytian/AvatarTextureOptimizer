// Copyright (c) fosa. Licensed under the MIT License.
// Pull-push infinite dilation. Colour is propagated outwards into unwritten texels so that
// bilinear filtering and mipmapping never sample background through an island edge, while
// alpha is preserved exactly: transparent stays transparent.
// Pull-push 无限外扩填充。将颜色向外传播到未写入的 texel，
// 使双线性过滤与 mipmap 永远不会透过岛边缘采样到背景，
// 同时 alpha 被精确保留：透明处仍保持透明。
Shader "Hidden/ATO/PullPush"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        float4 _MainTex_TexelSize;
        sampler2D _CoarseTex;
        float4 _CoarseTex_TexelSize;

        struct appdata
        {
            float4 vertex : POSITION;
            float2 uv : TEXCOORD0;
        };

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        v2f vert(appdata v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.uv;
            return o;
        }
        ENDCG

        // Pass 0: PULL. Downsample by 2, averaging only texels that carry valid coverage.
        // The weight channel accumulates coverage so partially-covered parents stay unbiased.
        // 通道 0：PULL。2 倍下采样，只对具备有效覆盖的 texel 求平均。
        // 权重通道累积覆盖度，使部分覆盖的父级不产生偏差。
        Pass
        {
            Name "Pull"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            float4 frag(v2f i) : SV_Target
            {
                float2 texel = _MainTex_TexelSize.xy;
                float2 base = i.uv - texel * 0.5;

                float4 s0 = tex2D(_MainTex, base + float2(0, 0));
                float4 s1 = tex2D(_MainTex, base + float2(texel.x, 0));
                float4 s2 = tex2D(_MainTex, base + float2(0, texel.y));
                float4 s3 = tex2D(_MainTex, base + float2(texel.x, texel.y));

                // .a here is the coverage weight, not the image alpha.
                // 此处的 .a 是覆盖权重，而非图像 alpha。
                float w = s0.a + s1.a + s2.a + s3.a;
                if (w <= 0.0)
                {
                    return float4(0, 0, 0, 0);
                }

                float3 rgb = (s0.rgb * s0.a + s1.rgb * s1.a + s2.rgb * s2.a + s3.rgb * s3.a) / w;
                return float4(rgb, saturate(w * 0.25));
            }
            ENDCG
        }

        // Pass 1: PUSH. Fill unwritten texels from the coarser level; keep written texels intact.
        // 通道 1：PUSH。用更粗一级的结果填充未写入的 texel；已写入的 texel 保持不变。
        Pass
        {
            Name "Push"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            float4 frag(v2f i) : SV_Target
            {
                float4 fine = tex2D(_MainTex, i.uv);
                if (fine.a >= 1.0)
                {
                    return fine;
                }

                float4 coarse = tex2D(_CoarseTex, i.uv);
                if (coarse.a <= 0.0)
                {
                    return fine;
                }

                // Blend the coarse estimate under whatever partial coverage already exists.
                // 将粗级估计混合到已有的部分覆盖之下。
                float3 rgb = fine.rgb * fine.a + coarse.rgb * (1.0 - fine.a);
                float a = saturate(fine.a + coarse.a * (1.0 - fine.a));
                return float4(rgb, a);
            }
            ENDCG
        }

        // Pass 2: RESOLVE. Combine the dilated colour with the original straight alpha, so
        // transparent regions keep alpha 0 while still carrying sensible RGB for filtering.
        // 通道 2：RESOLVE。将外扩后的颜色与原始直通 alpha 合并，
        // 使透明区域的 alpha 保持为 0，同时仍携带适合过滤的合理 RGB。
        Pass
        {
            Name "Resolve"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            sampler2D _OriginalTex;
            sampler2D _CoverageTex;

            float4 frag(v2f i) : SV_Target
            {
                float4 dilated = tex2D(_MainTex, i.uv);
                float4 original = tex2D(_OriginalTex, i.uv);

                // Coverage is tracked separately from image alpha. Using alpha as the coverage
                // test would misclassify a legitimately transparent texel INSIDE an island as
                // unwritten and overwrite its authored colour with dilated neighbours.
                // 覆盖度与图像 alpha 分开跟踪。若用 alpha 判断覆盖，
                // 会把岛**内部**本就透明的 texel 误判为未写入，
                // 从而用外扩的邻居颜色覆盖其原有颜色。
                float coverage = tex2D(_CoverageTex, i.uv).r;

                if (coverage > 0.0)
                {
                    return original;
                }

                // Outside every island: dilated colour, alpha forced to zero so the padding
                // never becomes visible geometry.
                // 位于所有岛之外：使用外扩颜色，alpha 强制为 0，
                // 使填充区域永远不会显现为可见内容。
                return float4(dilated.rgb, 0.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
