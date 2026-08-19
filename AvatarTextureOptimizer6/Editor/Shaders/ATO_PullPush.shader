// ATO_PullPush
// GPU push-pull（无限外扩）填充图集空白区域。
// 内容纹理 _MainTex + 覆盖掩码 _CoverageTex（0/1）。
// PushDown：覆盖加权平均下采样（把覆盖信息存进 alpha 输出）。
// PullUp：未覆盖区域用上一层（粗尺度）颜色回填；透明贴图未覆盖区 alpha 置 0。
Shader "ATO/PullPush"
{
    Properties
    {
        _MainTex ("Content", 2D) = "black" {}
        _CoverageTex ("Coverage", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        // ---------- Pass 0: PushDown（覆盖加权平均，输出到半尺寸 RT） ----------
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDown
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            sampler2D _CoverageTex;
            float4 _CoverageTex_TexelSize;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 fragDown(v2f i) : SV_Target
            {
                float2 texel = _MainTex_TexelSize.xy;
                float2 base = i.uv - texel * 0.5;
                float2 halfTexel = texel * 0.5;

                fixed4 c00 = tex2D(_MainTex, base);
                fixed4 c10 = tex2D(_MainTex, base + float2(texel.x, 0));
                fixed4 c01 = tex2D(_MainTex, base + float2(0, texel.y));
                fixed4 c11 = tex2D(_MainTex, base + float2(texel.x, texel.y));

                fixed cov00 = tex2D(_CoverageTex, base).a;
                fixed cov10 = tex2D(_CoverageTex, base + float2(texel.x, 0)).a;
                fixed cov01 = tex2D(_CoverageTex, base + float2(0, texel.y)).a;
                fixed cov11 = tex2D(_CoverageTex, base + float2(texel.x, texel.y)).a;

                fixed4 sumC = c00 * cov00 + c10 * cov10 + c01 * cov01 + c11 * cov11;
                fixed sumW = cov00 + cov10 + cov01 + cov11;

                fixed4 o;
                if (sumW > 1e-4)
                {
                    o.rgb = sumC.rgb / sumW;
                    o.a = sumC.a / sumW;
                }
                else
                {
                    o = fixed4(0, 0, 0, 0);
                }
                return o;
            }
            ENDCG
        }

        // ---------- Pass 1: PullUp（未覆盖区回填粗尺度颜色） ----------
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragUp
            #include "UnityCG.cginc"

            sampler2D _MainTex;      // 当前尺度内容（细）
            sampler2D _CoverageTex;  // 当前尺度覆盖
            sampler2D _CoarseTex;    // 上一层（粗）结果
            float _Transparent;      // 1 = 透明贴图（未覆盖区 alpha 0）

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 fragUp(v2f i) : SV_Target
            {
                fixed4 content = tex2D(_MainTex, i.uv);
                fixed cov = tex2D(_CoverageTex, i.uv).a;
                fixed4 coarse = tex2D(_CoarseTex, i.uv);

                fixed4 o;
                o.rgb = lerp(coarse.rgb, content.rgb, cov);
                if (_Transparent > 0.5)
                {
                    o.a = content.a * cov; // 未覆盖区 alpha 0
                }
                else
                {
                    o.a = 1.0;
                }
                return o;
            }
            ENDCG
        }
    }
    FallBack Off
}
