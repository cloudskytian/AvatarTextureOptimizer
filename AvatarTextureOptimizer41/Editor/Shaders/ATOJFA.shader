// ATO jump-flooding (JFA) edge-extrapolation shader ("infinite" pull-push fill).
// Pass 1 writes seeds (island pixels) with their position and the seed color, empty pixels get
// a sentinel. Middle passes propagate nearest seeds with halving stride. Final pass writes the
// nearest seed's color. Transparent textures keep alpha 0 (their seeds carry alpha 0 so the fill
// stays transparent).
// ATO 的跳 flood（JFA）边缘外扩 shader（"无限" pull-push 填充）。
// 第 1 遍写入种子（岛像素）及其位置与颜色，空白像素写哨兵；中间遍以减半步长传播最近种子；
// 最后一遍写出最近种子的颜色。透明贴图的 alpha 保持 0（种子 alpha 为 0，填充保持透明）。
Shader "Hidden/ATO/JFA"
{
    Properties
    {
        _MainTex ("Seeds (xy=pos, z=valid)", 2D) = "black" {}
        _ColorTex ("Seed Colors", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            // 0: seed pass. 种子遍。
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _Threshold = 0.00392157; // 1/255.

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;
                // A pixel is a seed when its alpha is above threshold (opaque region).
                // 当 alpha 高于阈值（不透明区域）时该像素为种子。
                fixed4 c = tex2D(_MainTex, uv);
                float valid = (c.a > _Threshold) ? 1.0 : 0.0;
                return fixed4(uv, valid, 1.0);
            }
            ENDCG
        }

        Pass
        {
            // 1: JFA propagation pass (stride in _JFAStride). 传播遍（步长 _JFAStride）。
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;  // current nearest-seed positions. 当前最近种子位置。
            float _JFAStride;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 best = float2(1e9, 1e9);
                float bestValid = 0.0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        float2 p = i.uv + float2(dx, dy) * _JFAStride * _MainTex_TexelSize.xy;
                        fixed4 s = tex2D(_MainTex, p);
                        if (s.z > 0.5)
                        {
                            float2 d = s.xy - i.uv;
                            float dist = dot(d, d);
                            if (dist < dot(best, best))
                            {
                                best = s.xy;
                                bestValid = 1.0;
                            }
                        }
                    }
                }
                return fixed4(best, bestValid, 1.0);
            }
            ENDCG
        }

        Pass
        {
            // 2: final gather: write nearest seed color. 最终汇聚：写出最近种子颜色。
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;   // nearest-seed positions. 最近种子位置。
            sampler2D _ColorTex;  // original content (seed colors). 原始内容（种子颜色）。
            float4 _ColorTex_TexelSize;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 s = tex2D(_MainTex, i.uv);
                if (s.z > 0.5)
                    return tex2D(_ColorTex, s.xy);
                return fixed4(0,0,0,0);
            }
            ENDCG
        }
    }
    Fallback Off
}
