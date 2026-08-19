Shader "Hidden/ATO/Decode"
{
    // Decodes any sampled texture into linear RGBA float. When _AsNormal is set the texel is
    // interpreted as a Unity tangent normal (handles DXT5nm/AG packing), renormalized and stored
    // as xyz*0.5+0.5. / 解码任意贴图为线性RGBA；法线模式做UnpackNormal+重归一化再编码回0..1。
    Properties { _MainTex ("Texture", 2D) = "white" {} _AsNormal ("As Normal", Float) = 0 }
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
            float _AsNormal;

            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                if (_AsNormal > 0.5)
                {
                    float3 n = UnpackNormal(c);
                    n = normalize(n);
                    return float4(n * 0.5 + 0.5, 1);
                }
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
