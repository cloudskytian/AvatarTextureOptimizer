// ATO GPU blit/resample shader with UV transform.
// Used for: linear-space resampling of island regions, premultiplied-alpha downsampling,
// and atlas baking (island content -> atlas rect).
// ATO 的 GPU blit/重采样 shader（带 UV 变换）。用于：岛区域的线性空间重采样、
// 预乘 alpha 下采样、图集烘焙（岛内容 → 图集矩形）。
Shader "Hidden/ATO/Blit"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            // 0: plain bilinear sample with UV scale/offset (UV = uv * _UVScale + _UVOffset).
            //    双线性采样，UV = uv * _UVScale + _UVOffset。
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float2 _UVScale = float2(1,1);
            float2 _UVOffset = float2(0,0);

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv * _UVScale + _UVOffset;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv);
            }
            ENDCG
        }

        Pass
        {
            // 1: premultiplied-alpha aware sample: color premultiplied by alpha before storing.
            //    预乘 alpha 采样：存储前将颜色乘以 alpha。
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float2 _UVScale = float2(1,1);
            float2 _UVOffset = float2(0,0);

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv * _UVScale + _UVOffset;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }

        Pass
        {
            // 2: 90° clockwise rotated sample (used to bake islands packed with rotation=1).
            //    90° 顺时针旋转采样（用于烘焙旋转=1 的岛）。
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float2 _UVScale = float2(1,1);
            float2 _UVOffset = float2(0,0);

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
                // rect uv (u,v) -> content uv (u0 + v*du, v0 + (1-u)*dv) 见 AtlasTextureBaker 注释。
                float2 uv2 = float2(_UVOffset.x + i.uv.y * _UVScale.x, _UVOffset.y + (1.0 - i.uv.x) * _UVScale.y);
                return tex2D(_MainTex, uv2);
            }
            ENDCG
        }

        Pass
        {
            // 3: composite: keep content where alpha > 0, else take the fill texture.
            //    合成：alpha>0 处保留内容，否则取填充贴图。
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            sampler2D _BlendTex;
            float _AlphaEps = 1.0/255.0;

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
                fixed4 c = tex2D(_MainTex, i.uv);
                if (c.a > _AlphaEps) return c;
                return tex2D(_BlendTex, i.uv);
            }
            ENDCG
        }

        Pass
        {
            // 4: bake island content into one atlas rect (no rotation): sample the texture region at
            //    (regionMin + p*regionSize) where p is the position inside the rect; outside the rect -> 0.
            //    将岛内容烘焙进单个图集矩形（无旋转）：在矩形内按 p 采样纹理区域；矩形外输出 0。
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Rect;      // rect in atlas UV (x,y,w,h). 图集 UV 中的矩形。
            float2 _UVScale = float2(1,1);
            float2 _UVOffset = float2(0,0);

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
                float2 p = (i.uv - _Rect.xy) / max(_Rect.zw, 1e-6);
                if (any(p < 0.0) || any(p > 1.0)) return fixed4(0,0,0,0);
                return tex2D(_MainTex, _UVOffset + p * _UVScale);
            }
            ENDCG
        }

        Pass
        {
            // 5: bake island content into one atlas rect with 90° CW rotation (matches pack rotation=1).
            //    带 90° 顺时针旋转的岛内容烘焙（对应装箱旋转=1）。
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Rect;
            float2 _UVScale = float2(1,1);
            float2 _UVOffset = float2(0,0);

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
                float2 p = (i.uv - _Rect.xy) / max(_Rect.zw, 1e-6);
                if (any(p < 0.0) || any(p > 1.0)) return fixed4(0,0,0,0);
                // p=(pu,pv): content uv = (regionMinU + pv*regionW, regionMinV + (1-pu)*regionH).
                float2 uv2 = float2(_UVOffset.x + p.y * _UVScale.x, _UVOffset.y + (1.0 - p.x) * _UVScale.y);
                return tex2D(_MainTex, uv2);
            }
            ENDCG
        }
    }
    Fallback Off
}
