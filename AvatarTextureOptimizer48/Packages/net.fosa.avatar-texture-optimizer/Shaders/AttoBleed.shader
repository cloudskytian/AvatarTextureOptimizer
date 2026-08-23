// ATO pull-push bleed shader: dilates island edge colors into empty atlas areas (approximate pull-push).
// Transparent atlases keep alpha = 0 outside islands (per spec). Known color-bleeding artifact is accepted.
// / ATO pull-push 外扩着色器：把岛边缘颜色外扩填充到图集空白区域（近似 pull-push）。
// 透明图集在岛外保持 alpha=0（按规格）。已知的渗色问题可接受。
Shader "ATO/Bleed"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
        _MaskTex ("Coverage", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" }
        Blend Off
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _MaskTex;
            float4 _MainTex_TexelSize;

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

            fixed4 frag(v2f i) : SV_Target
            {
                float mself = tex2D(_MaskTex, i.uv).a;
                if (mself > 0.5)
                {
                    return tex2D(_MainTex, i.uv);
                }

                float4 acc = 0;
                float w = 0;
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        float2 uv = i.uv + float2(dx, dy) * _MainTex_TexelSize.xy;
                        float m = tex2D(_MaskTex, uv).a;
                        if (m > 0.5)
                        {
                            acc += tex2D(_MainTex, uv);
                            w += 1.0;
                        }
                    }
                }
                if (w > 0)
                {
                    // transparent atlases: alpha stays 0 outside islands / 透明图集：岛外 alpha 保持 0
                    return float4(acc.rgb / w, 0.0);
                }
                return tex2D(_MainTex, i.uv);
            }
            ENDCG
        }
    }
    Fallback Off
}
