// ATO 法线图集重归一化：外扩填充后对全图解码→归一化→编码（已归一像素幂等）。
// ATO normal-atlas renormalization: decode → normalize → encode over the whole atlas (idempotent for normalized pixels).
Shader "Hidden/ATO/NormalizeNormal"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            fixed4 frag (v2f_img i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                float3 n = c.rgb * 2.0 - 1.0;
                float len = length(n);
                n = len > 0.0001 ? n / len : float3(0, 0, 1);
                return fixed4(n * 0.5 + 0.5, 1.0);
            }
            ENDCG
        }
    }
}
