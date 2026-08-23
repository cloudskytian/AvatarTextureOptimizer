Shader "Hidden/ATO/AtlasBlit"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            int _Semantic;
            int _AreaSample;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata value) { v2f output; output.vertex = UnityObjectToClipPos(value.vertex); output.uv = value.uv; return output; }

            float4 frag(v2f input) : SV_Target
            {
                float2 d = _MainTex_TexelSize.xy * 0.25;
                float4 a;
                float4 b;
                float4 c;
                float4 e;
                if (_AreaSample != 0)
                {
                    a = tex2D(_MainTex, input.uv + float2(-d.x, -d.y));
                    b = tex2D(_MainTex, input.uv + float2( d.x, -d.y));
                    c = tex2D(_MainTex, input.uv + float2(-d.x,  d.y));
                    e = tex2D(_MainTex, input.uv + float2( d.x,  d.y));
                }
                else
                {
                    a = tex2D(_MainTex, input.uv);
                    b = a; c = a; e = a;
                }
                if (_Semantic == 2)
                {
                    float3 normal = normalize(UnpackNormal(a) + UnpackNormal(b) + UnpackNormal(c) + UnpackNormal(e));
                    return float4(normal * 0.5 + 0.5, 1.0);
                }
                if (_Semantic == 1)
                {
                    float alpha = (a.a + b.a + c.a + e.a) * 0.25;
                    float3 premultiplied = (a.rgb * a.a + b.rgb * b.a + c.rgb * c.a + e.rgb * e.a) * 0.25;
                    return float4(alpha > 1e-7 ? premultiplied / alpha : 0, alpha);
                }
                return (a + b + c + e) * 0.25;
            }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragMask
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; };
            v2f vert(appdata value) { v2f output; output.vertex = UnityObjectToClipPos(value.vertex); return output; }
            float4 fragMask(v2f input) : SV_Target { return 1; }
            ENDCG
        }
    }
    Fallback Off
}
