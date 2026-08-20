Shader "Hidden/ATO/PullPush"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _KeepAlphaZero;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }
            // One pull-push iteration: fill empty (a==0 && rgb==0) from neighbors.
            // 一次 pull-push：用邻域填充空像素。透明贴图保持 alpha=0。
            fixed4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                float2 t = _MainTex_TexelSize.xy;
                float4 acc = 0;
                float n = 0;
                float4 s;
                s = tex2D(_MainTex, i.uv + float2(-t.x, 0)); if (any(s.rgb)) { acc += s; n++; }
                s = tex2D(_MainTex, i.uv + float2( t.x, 0)); if (any(s.rgb)) { acc += s; n++; }
                s = tex2D(_MainTex, i.uv + float2(0, -t.y)); if (any(s.rgb)) { acc += s; n++; }
                s = tex2D(_MainTex, i.uv + float2(0,  t.y)); if (any(s.rgb)) { acc += s; n++; }
                if (any(c.rgb) || n < 1) return c;
                acc /= n;
                if (_KeepAlphaZero > 0.5) acc.a = 0;
                return acc;
            }
            ENDCG
        }
    }
}
