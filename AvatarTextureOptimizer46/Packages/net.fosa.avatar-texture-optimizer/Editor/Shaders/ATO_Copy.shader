// SPDX-License-Identifier: MIT
// EN: Plain copy with an explicit scale/offset, avoiding Graphics.Blit's platform quirks.
// ZH: 带显式缩放/偏移的简单拷贝，规避 Graphics.Blit 的平台差异问题。
Shader "Hidden/ATO/Copy"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} _ATO_ScaleOffset ("Scale/Offset", Vector) = (1,1,0,0) }
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
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert (appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord * _ATO_ScaleOffset.xy + _ATO_ScaleOffset.zw;
                return o;
            }
            float4 frag (v2f i) : SV_Target { return tex2D(_MainTex, i.uv); }
            ENDCG
        }
    }
}
