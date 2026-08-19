// ATO_Resample
// 线性空间重采样（手动 4-tap 双线性，支持透明贴图预乘 alpha 下采样），
// 支持把源贴图区域写入图集任意矩形（含 90° 旋转的 UV 变换），
// 支持末尾线性→sRGB 编码（sRGB 图集时开启 _OutputGamma）。
Shader "ATO/Resample"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "PreviewType"="Plane" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            // 目标矩形（clip 空间）：clipPos = (uv*2-1) * _DestScale + _DestOffset
            float4 _DestScale;
            float4 _DestOffset;
            // 源区域变换（uv → 源贴图 uv）
            float4 _SrcScale;
            float4 _SrcBias;
            // 1 = 预乘 alpha 下采样（采样时乘 alpha，输出时除以 alpha）
            float _Premultiply;
            // 1 = 输出线性→sRGB
            float _OutputGamma;
            // 1 = 90° 旋转的源采样（srcU = scale.x*(1-v)+bias.x, srcV = scale.y*u+bias.y）
            float _SrcRotate;
            // 1 = 源为 sRGB 且工程为 Gamma 色彩空间（需手动转线性，保证确定性）
            float _SourceSRGB;

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
                // Graphics.Blit 的全屏四边形 uv 覆盖 0..1（顶点坐标已是裁剪空间 -1..1）
                // 目标矩形：clip = (uv*2-1) * _DestScale + _DestOffset
                float2 clip = (v.uv * 2.0 - 1.0) * _DestScale.xy + _DestOffset.xy;
                o.pos = float4(clip, 0.0, 1.0);
                o.uv = v.uv; // 源映射全部在 fragment 中完成（含旋转），避免双重变换
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv;
                if (_SrcRotate > 0.5)
                {
                    uv = float2(_SrcScale.x * (1.0 - i.uv.y) + _SrcBias.x,
                                _SrcScale.y * i.uv.x + _SrcBias.y);
                }
                else
                {
                    uv = i.uv * _SrcScale.xy + _SrcBias.xy;
                }
                float2 texel = _MainTex_TexelSize.xy;
                float2 p = uv / texel - 0.5;          // 以像素为单位的位置
                float2 f = frac(p);
                float2 base = (floor(p) + 0.5) * texel;

                fixed4 c00 = tex2D(_MainTex, base);
                fixed4 c10 = tex2D(_MainTex, base + float2(texel.x, 0));
                fixed4 c01 = tex2D(_MainTex, base + float2(0, texel.y));
                fixed4 c11 = tex2D(_MainTex, base + float2(texel.x, texel.y));

                if (_SourceSRGB > 0.5)
                {
                    c00.rgb = GammaToLinearSpace(c00.rgb);
                    c10.rgb = GammaToLinearSpace(c10.rgb);
                    c01.rgb = GammaToLinearSpace(c01.rgb);
                    c11.rgb = GammaToLinearSpace(c11.rgb);
                }

                if (_Premultiply > 0.5)
                {
                    c00.rgb *= c00.a; c10.rgb *= c10.a; c01.rgb *= c01.a; c11.rgb *= c11.a;
                }

                fixed4 outC = lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);

                if (_Premultiply > 0.5)
                {
                    float a = max(outC.a, 1e-5);
                    outC.rgb /= a;
                }

                if (_OutputGamma > 0.5)
                {
                    outC.rgb = LinearToGammaSpace(outC.rgb);
                }
                return outC;
            }
            ENDCG
        }
    }
    FallBack Off
}
