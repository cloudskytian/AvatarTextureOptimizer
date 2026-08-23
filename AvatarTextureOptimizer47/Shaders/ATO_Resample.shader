Shader "Hidden/ATO/LinearResample"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _UvRect;
            int _Mode;
            int _Premultiply;
            int _AreaSample;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata value)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(value.vertex);
                output.uv = value.uv;
                return output;
            }

            float4 frag(v2f input) : SV_Target
            {
                float2 uv = _UvRect.xy + input.uv * _UvRect.zw;
                float2 d = _MainTex_TexelSize.xy * 0.25;
                float4 a;
                float4 b;
                float4 c;
                float4 e;
                if (_AreaSample != 0)
                {
                    a = tex2D(_MainTex, uv + float2(-d.x, -d.y));
                    b = tex2D(_MainTex, uv + float2( d.x, -d.y));
                    c = tex2D(_MainTex, uv + float2(-d.x,  d.y));
                    e = tex2D(_MainTex, uv + float2( d.x,  d.y));
                }
                else
                {
                    a = tex2D(_MainTex, uv);
                    b = a; c = a; e = a;
                }
                if (_Mode == 1)
                {
                    // EN: Decode imported normal layout, average, renormalize and encode RGB.
                    // ZH: 解码导入法线布局、平均、重新归一化并编码到 RGB。
                    float3 normal = normalize(UnpackNormal(a) + UnpackNormal(b) + UnpackNormal(c) + UnpackNormal(e));
                    return float4(normal * 0.5 + 0.5, 1.0);
                }
                if (_Mode == 2)
                {
                    // EN: Candidate render textures already use RGB normal encoding.
                    // ZH: 候选 RenderTexture 已使用 RGB 法线编码。
                    float3 normal = normalize((a.xyz + b.xyz + c.xyz + e.xyz) * 2.0 - 4.0);
                    return float4(normal * 0.5 + 0.5, 1.0);
                }
                if (_Premultiply != 0)
                {
                    a.rgb *= a.a; b.rgb *= b.a; c.rgb *= c.a; e.rgb *= e.a;
                }
                return (a + b + c + e) * 0.25;
            }
            ENDCG
        }
    }
    Fallback Off
}
