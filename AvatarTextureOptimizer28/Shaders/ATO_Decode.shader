// EN: Colour-space aware blit used for every texture decode and encode in ATO.
//     Blitting through the GPU is what lets us read Crunch / BCn / non-readable textures without
//     touching the user's TextureImporter.
// ZH: ATO 中所有贴图解码与编码所使用的、感知色彩空间的 blit。
//     通过 GPU 做 blit 正是我们能在不改动用户 TextureImporter 的前提下
//     读取 Crunch / BCn / 不可读贴图的原因。
Shader "Hidden/ATO/Decode"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Linearize ("sRGB to Linear", Float) = 0
        _Delinearize ("Linear to sRGB", Float) = 0
    }
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
            float4 _MainTex_ST;
            float _Linearize;
            float _Delinearize;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // EN: Exact IEC 61966-2-1 transfer functions. Unity's built-in helpers switch behaviour with
            //     the project colour space, which would make our metrics non-deterministic.
            // ZH: 严格的 IEC 61966-2-1 传输函数。Unity 内置辅助函数会随工程色彩空间改变行为，
            //     那会让我们的度量结果不确定。
            float3 SRGBToLinearExact(float3 c)
            {
                return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
            }
            float3 LinearToSRGBExact(float3 c)
            {
                c = max(c, 0.0);
                return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
            }

            float4 frag (v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                if (_Linearize > 0.5) c.rgb = SRGBToLinearExact(saturate(c.rgb));
                if (_Delinearize > 0.5) c.rgb = LinearToSRGBExact(c.rgb);
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
