// ATO GPU passes: region resample (linear-space, premultiplied-aware), raw copy,
//! pull-push bleed pyramid. / ATO GPU 通道：区域重采样（线性空间+预乘）、原始拷贝、pull-push 渗色金字塔。
Shader "Hidden/ATO/Gfx"
{
    Properties
    {
        _MainTex ("", 2D) = "white" {}
        _PrevTex ("", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // ---------------------------------------------------------- Pass 0: resample
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ ATO_LINEARIZE
            #pragma multi_compile _ ATO_PREMUL
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
            #ifdef ATO_LINEARIZE
                c.rgb = saturate(c.rgb);
                c.rgb = c.rgb <= 0.04045 ? c.rgb / 12.92 : pow((c.rgb + 0.055) / 1.055, 2.4);
            #endif
            #ifdef ATO_PREMUL
                c.rgb *= c.a;
            #endif
                return c;
            }
            ENDCG
        }

        // ---------------------------------------------------------- Pass 1: raw copy
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }
            float4 frag(v2f i) : SV_Target { return tex2D(_MainTex, i.uv); }
            ENDCG
        }

        // ---------------------------------------------------------- Pass 2: pull
        // Weighted downsample: color = sum(c*w)/sum(w), w = coverage. / 加权下采样（按覆盖度）。
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }
            float4 frag(v2f i) : SV_Target
            {
                float2 t = _MainTex_TexelSize.xy;
                float4 a = tex2D(_MainTex, i.uv + t * float2(-0.5, -0.5));
                float4 b = tex2D(_MainTex, i.uv + t * float2( 0.5, -0.5));
                float4 c = tex2D(_MainTex, i.uv + t * float2(-0.5,  0.5));
                float4 d = tex2D(_MainTex, i.uv + t * float2( 0.5,  0.5));
                float w = a.a + b.a + c.a + d.a;
                if (w > 1e-4)
                {
                    float3 col = (a.rgb * a.a + b.rgb * b.a + c.rgb * c.a + d.rgb * d.a) / w;
                    return float4(col, saturate(w * 0.25));
                }
                return float4(0, 0, 0, 0);
            }
            ENDCG
        }

        // ---------------------------------------------------------- Pass 3: push
        // Fill uncovered pixels from the smaller level. / 用更小层级填补未覆盖像素。
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            sampler2D _PrevTex;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }
            float4 frag(v2f i) : SV_Target
            {
                float4 cur = tex2D(_MainTex, i.uv);
                float4 prev = tex2D(_PrevTex, i.uv);
                if (cur.a < 0.5)
                    return float4(prev.rgb, cur.a); // keep own (zero) alpha / 保留自身 alpha
                return cur;
            }
            ENDCG
        }
    }
    Fallback Off
}
