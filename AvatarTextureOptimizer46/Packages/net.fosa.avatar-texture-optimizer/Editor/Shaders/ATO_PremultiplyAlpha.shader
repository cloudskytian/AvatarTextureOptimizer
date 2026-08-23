// SPDX-License-Identifier: MIT
// EN: Premultiplies or un-premultiplies alpha while optionally cropping. Used so that box filtering a
//     transparent texture does not pull colour out of fully transparent texels.
// ZH: 在可选裁剪的同时预乘或反预乘 alpha。用于避免对透明贴图做盒式滤波时，
//     把全透明像素的颜色拉进来。
Shader "Hidden/ATO/PremultiplyAlpha"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _ATO_ScaleOffset ("Scale/Offset", Vector) = (1,1,0,0)
        _ATO_Mode ("0 = premultiply, 1 = unpremultiply", Float) = 0
    }
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
            float4 _ATO_ScaleOffset;
            float _ATO_Mode;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord * _ATO_ScaleOffset.xy + _ATO_ScaleOffset.zw;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                if (_ATO_Mode < 0.5)
                {
                    // EN: premultiply / ZH: 预乘
                    c.rgb *= c.a;
                }
                else
                {
                    // EN: unpremultiply, guarding against a zero divide
                    // ZH: 反预乘，避免除零
                    c.rgb = c.a > 1e-5 ? c.rgb / c.a : 0.0;
                }
                return c;
            }
            ENDCG
        }
    }
}
