Shader "Hidden/ATO/PullPush"
{
    Properties { _MainTex ("Tex", 2D) = "white" {} _KeepAlphaZero ("KeepAlphaZero", Float) = 0 }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Name "Pull"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
            v2f vert(appdata v){ v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o; }
            float4 frag(v2f i):SV_Target
            {
                // Average valid (a>0 or rgb length) neighbours at 2x downsample source.
                float2 t = _MainTex_TexelSize.xy;
                float4 acc = 0; float n = 0;
                [unroll] for (int y=0;y<2;y++)
                [unroll] for (int x=0;x<2;x++)
                {
                    float4 c = tex2D(_MainTex, i.uv + float2((x-0.5)*t.x,(y-0.5)*t.y));
                    if (any(c.rgb) || c.a > 0.001) { acc += c; n += 1; }
                }
                if (n < 1) return 0;
                return acc / n;
            }
            ENDCG
        }
        Pass
        {
            Name "Push"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            sampler2D _Low;
            float _KeepAlphaZero;
            struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
            v2f vert(appdata v){ v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o; }
            float4 frag(v2f i):SV_Target
            {
                float4 hi = tex2D(_MainTex, i.uv);
                if (any(hi.rgb) || hi.a > 0.001) return hi;
                float4 lo = tex2D(_Low, i.uv);
                if (_KeepAlphaZero > 0.5) lo.a = 0;
                return lo;
            }
            ENDCG
        }
    }
}
