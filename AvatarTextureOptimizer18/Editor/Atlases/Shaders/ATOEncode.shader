// ATO 图集编码：线性预乘 → （可选）去预乘 + sRGB 编码字节（写入 ARGB32 目标）。
// ATO atlas encode: linear premultiplied → (optional) unpremultiply + sRGB-encoded bytes (into an ARGB32 target).
Shader "Hidden/ATO/Encode"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _SRGB ("SRGB", Float) = 1
        _Unpremultiply ("Unpremultiply", Float) = 1
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
            float _SRGB;
            float _Unpremultiply;

            float3 LinearToSRGBExact(float3 c)
            {
                c = max(c, 0.0);
                float3 lo = c * 12.92;
                float3 hi = 1.055 * pow(c, 1.0 / 2.4) - 0.055;
                return lerp(hi, lo, step(c, 0.0031308));
            }

            fixed4 frag (v2f_img i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                float a = c.a;
                float3 rgb = c.rgb;
                if (_Unpremultiply > 0.5)
                {
                    rgb = a > 0.0001 ? rgb / a : 0.0;
                }
                if (_SRGB > 0.5)
                {
                    rgb = LinearToSRGBExact(rgb);
                }
                return fixed4(rgb, a);
            }
            ENDCG
        }
    }
}
