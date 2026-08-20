// AvatarTextureOptimizer - ATOBlit
// EN: Island copy & dilation shader. Passes:
//   0: bilinear direct (opaque)          1: premultiplied 2x2 box (blend)
//   2: normal decode/resample/re-encode  3: (reserved)
//   4: 3x3 dilation (pull-push fallback)
// CN: 岛拷贝与扩张着色器。通道：
//   0: 双线性直拷（不透明）  1: 预乘 2x2 盒式（Blend）
//   2: 法线解码/重采样/重编码  3:（保留）
//   4: 3x3 扩张（pull-push 回退）
Shader "Hidden/ATO/Blit"
{
    Properties { _MainTex ("Source", 2D) = "white" {} }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off ZWrite Off ZTest Always
        Blend Off

        Pass // 0: direct bilinear
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _SrcRect;   // xy=min zw=size (uv)
            float2 _DestSize;  // 目标矩形像素
            float _Rotate;     // 0/90/180/270
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }
            float2 RotUV(float2 uv)
            {
                if (_Rotate >= 90.0 && _Rotate < 180.0) return float2(1.0 - uv.y, uv.x);
                if (_Rotate >= 180.0 && _Rotate < 270.0) return float2(1.0 - uv.x, 1.0 - uv.y);
                if (_Rotate >= 270.0) return float2(uv.y, 1.0 - uv.x);
                return uv;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = _SrcRect.xy + RotUV(i.uv) * _SrcRect.zw;
                return tex2D(_MainTex, uv);
            }
            ENDHLSL
        }

        Pass // 1: premultiplied 2x2 box average (blend)
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _SrcRect;
            float2 _DestSize;
            float _Rotate;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }
            float2 RotUV(float2 uv)
            {
                if (_Rotate >= 90.0 && _Rotate < 180.0) return float2(1.0 - uv.y, uv.x);
                if (_Rotate >= 180.0 && _Rotate < 270.0) return float2(1.0 - uv.x, 1.0 - uv.y);
                if (_Rotate >= 270.0) return float2(uv.y, 1.0 - uv.x);
                return uv;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = _SrcRect.xy + RotUV(i.uv) * _SrcRect.zw;
                float2 step = _SrcRect.zw / max(float2(1,1), _DestSize);
                float2 o = step * 0.25;
                float4 acc = 0;
                float2 t;
                t = uv + float2(-o.x, -o.y); float4 s0 = tex2D(_MainTex, t);
                t = uv + float2(o.x, -o.y);  float4 s1 = tex2D(_MainTex, t);
                t = uv + float2(-o.x, o.y);  float4 s2 = tex2D(_MainTex, t);
                t = uv + float2(o.x, o.y);   float4 s3 = tex2D(_MainTex, t);
                // premultiply
                s0.rgb *= s0.a; s1.rgb *= s1.a; s2.rgb *= s2.a; s3.rgb *= s3.a;
                acc = (s0 + s1 + s2 + s3) * 0.25;
                // unpremultiply
                if (acc.a > 1e-5) acc.rgb /= acc.a;
                return acc;
            }
            ENDHLSL
        }

        Pass // 2: normal decode -> bilinear -> renormalize -> encode
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _SrcRect;
            float2 _DestSize;
            float _Rotate;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }
            float2 RotUV(float2 uv)
            {
                if (_Rotate >= 90.0 && _Rotate < 180.0) return float2(1.0 - uv.y, uv.x);
                if (_Rotate >= 180.0 && _Rotate < 270.0) return float2(1.0 - uv.x, 1.0 - uv.y);
                if (_Rotate >= 270.0) return float2(uv.y, 1.0 - uv.x);
                return uv;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = _SrcRect.xy + RotUV(i.uv) * _SrcRect.zw;
                float4 c = tex2D(_MainTex, uv);
                // EN: Unity standard normal decoding (in the texture's stored space).
                // CN: Unity 标准法线解码（贴图存储空间内）。
                float3 n = c.rgb * 2.0 - 1.0;
                float l = length(n);
                if (l < 1e-5) n = float3(0, 0, 1); else n /= l;
                // encode
                n = n * 0.5 + 0.5;
                return float4(n, c.a);
            }
            ENDHLSL
        }

        Pass // 3: reserved
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }
            fixed4 frag(v2f i) : SV_Target { return tex2D(_MainTex, i.uv); }
            ENDHLSL
        }

        Pass // 4: 3x3 dilation (pull-push fallback; radius in texels)
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            int _DilateRadius;
            int _Transparent; // 1 = 透明图集（alpha 保持 0）
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }
            fixed4 frag(v2f i) : SV_Target
            {
                float4 self = tex2D(_MainTex, i.uv);
                bool empty = _Transparent == 1 ? self.a < 0.01 : (self.r + self.g + self.b + self.a) < 0.01;
                if (!empty) return self;
                float2 texel = _MainTex_TexelSize.xy * _DilateRadius;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        float4 s = tex2D(_MainTex, i.uv + float2(dx, dy) * texel);
                        bool sEmpty = _Transparent == 1 ? s.a < 0.01 : (s.r + s.g + s.b + s.a) < 0.01;
                        if (!sEmpty)
                        {
                            if (_Transparent == 1) s.a = 0;
                            return s;
                        }
                    }
                }
                return self;
            }
            ENDHLSL
        }
    }
}
