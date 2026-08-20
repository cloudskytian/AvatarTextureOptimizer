Shader "Hidden/ATO/Processing"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} _ST ("ST", Vector) = (1,1,0,0) }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        // Pass 0: crop by _ST (uv * st.xy + st.zw)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _ST;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }
            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv * _ST.xy + _ST.zw;
                return tex2D(_MainTex, uv);
            }
            ENDCG
        }
        // Pass 1: bilinear resample (linear-space friendly if RT is linear)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }
            float4 frag(v2f i) : SV_Target { return tex2D(_MainTex, i.uv); }
            ENDCG
        }
        // Pass 2: premultiplied-alpha downsample
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }
            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
        // Pass 3: pull-push dilate (copy nearest non-zero alpha / any color)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }
            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                if (c.a > 0.0001) return c;
                float4 acc = 0; float wsum = 0;
                [unroll] for (int y = -1; y <= 1; y++)
                [unroll] for (int x = -1; x <= 1; x++)
                {
                    if (x == 0 && y == 0) continue;
                    float4 n = tex2D(_MainTex, i.uv + float2(x, y) * _MainTex_TexelSize.xy);
                    if (n.a > 0.0001) { acc += n; wsum += 1; }
                }
                if (wsum > 0)
                {
                    acc /= wsum;
                    acc.a = 0; // keep transparent / 透明贴图 alpha 保持 0
                    return acc;
                }
                return c;
            }
            ENDCG
        }
        // Pass 4: blit island with optional 90 deg CW and normal XY remap
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _ST;
            float _Rotate90;
            float _IsNormal;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }
            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                if (_Rotate90 > 0.5)
                    uv = float2(uv.y, 1.0 - uv.x); // inverse of 90 CW / 90 度顺时针的逆
                uv = uv * _ST.xy + _ST.zw;
                float4 c = tex2D(_MainTex, uv);
                if (_IsNormal > 0.5 && _Rotate90 > 0.5)
                {
                    // Mesh tangents unchanged. Rotate tangent-space XY with the UV.
                    // 网格切线不重算。随 UV 旋转切线空间 XY。
                    float2 n = c.xy * 2 - 1;
                    float2 nr = float2(-n.y, n.x); // 90 CW
                    c.xy = nr * 0.5 + 0.5;
                }
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
