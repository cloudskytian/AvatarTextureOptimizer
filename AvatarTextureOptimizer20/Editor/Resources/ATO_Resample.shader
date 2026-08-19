Shader "Hidden/ATO/Resample"
{
    // Pass 0: premultiply alpha (linear). Pass 1: straight copy (bilinear via sampler).
    // Pass 2: unpremultiply. Downsampling chain: premultiply -> hardware bilinear resize -> unpremultiply.
    // 预乘alpha下采样链：预乘 -> 硬件双线性缩放 -> 反预乘。
    Properties { _MainTex ("Texture", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass // 0: premultiply
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                return float4(c.rgb * c.a, c.a);
            }
            ENDCG
        }

        Pass // 1: copy (used for resize blits)
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 frag(v2f_img i) : SV_Target { return tex2D(_MainTex, i.uv); }
            ENDCG
        }

        Pass // 2: unpremultiply
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                float3 rgb = c.a > 1e-6 ? c.rgb / c.a : 0;
                return float4(rgb, c.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
