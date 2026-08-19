// ============================================================================
// AvatarTextureOptimizer — Pull-Push 外扩填充着色器 / Pull-push inpainting shader
// 用于填充图集空白区域（无限外扩语义：push 反复膨胀 + pull 平滑）。
// For filling empty atlas regions (infinite extrapolation: iterative push + pull).
// Pass 0: Push — 8 邻域膨胀，取 alpha 最大的邻居颜色（覆盖度=alpha）。
// Pass 1: Pull — 5x5 均值模糊（平滑 push 造成的接缝）。
// ============================================================================
Shader "Hidden/ATO/PullPush"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "PUSH"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _Step; // 步长（texel） / step size

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 s = _MainTex_TexelSize.xy * _Step;
                fixed4 best = tex2D(_MainTex, i.uv);
                fixed4 c;
                c = tex2D(_MainTex, i.uv + float2( s.x,  0.0)); if (c.a > best.a) best = c;
                c = tex2D(_MainTex, i.uv + float2(-s.x,  0.0)); if (c.a > best.a) best = c;
                c = tex2D(_MainTex, i.uv + float2( 0.0,  s.y)); if (c.a > best.a) best = c;
                c = tex2D(_MainTex, i.uv + float2( 0.0, -s.y)); if (c.a > best.a) best = c;
                c = tex2D(_MainTex, i.uv + float2( s.x,  s.y)); if (c.a > best.a) best = c;
                c = tex2D(_MainTex, i.uv + float2(-s.x,  s.y)); if (c.a > best.a) best = c;
                c = tex2D(_MainTex, i.uv + float2( s.x, -s.y)); if (c.a > best.a) best = c;
                c = tex2D(_MainTex, i.uv + float2(-s.x, -s.y)); if (c.a > best.a) best = c;
                return best;
            }
            ENDCG
        }

        Pass
        {
            Name "PULL"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 s = _MainTex_TexelSize.xy;
                fixed4 sum = fixed4(0,0,0,0);
                const float w[5] = {0.1, 0.2, 0.4, 0.2, 0.1};
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        float2 uv = i.uv + float2(s.x * dx, s.y * dy);
                        fixed4 c = tex2D(_MainTex, uv);
                        sum += c * (w[dx+2] * w[dy+2]);
                    }
                }
                return sum;
            }
            ENDCG
        }
    }
    Fallback Off
}
