Shader "Hidden/ATO/Finalize"
{
    Properties { _MainTex ("Texture", 2D) = "black" {} }
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
            int _EncodeSrgb;
            float4 frag(v2f_img input) : SV_Target
            {
                float4 value = tex2D(_MainTex, input.uv);
                if (_EncodeSrgb != 0) value.rgb = LinearToGammaSpaceExact(max(value.rgb, 0));
                return value;
            }
            ENDCG
        }
    }
    Fallback Off
}
