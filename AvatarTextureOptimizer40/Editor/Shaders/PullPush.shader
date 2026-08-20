Shader "Hidden/Fosa/ATO/PullPush"
{
    // GPU pull-push dilation used to bleed island colors into atlas padding.
    // Transparent islands keep alpha 0 (we dilate RGB but leave alpha where it was 0).
    // 用于岛边缘向图集 padding 渗色的 GPU pull-push。透明岛保持 alpha 为 0。
    Properties { _MainTex ("", 2D) = "" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Name "Pull"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag_pull
            #include "UnityCG.cginc"
            sampler2D _MainTex; float4 _MainTex_TexelSize;
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
            v2f vert(appdata_img v){ v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.uv=v.texcoord; return o; }
            half4 frag_pull(v2f i):SV_Target
            {
                float4 c = tex2D(_MainTex,i.uv);
                if (c.a > 0.001) return c;
                float4 acc=0; float wsum=0;
                [unroll]
                for (int y=-1;y<=1;y++) for(int x=-1;x<=1;x++)
                {
                    float4 s=tex2D(_MainTex,i.uv+float2(x,y)*_MainTex_TexelSize.xy);
                    float w=s.a;
                    acc += s*w; wsum+=w;
                }
                if (wsum>0.0001){ float4 r=acc/wsum; r.a=0; return r; }
                return c;
            }
            ENDCG
        }
        Pass
        {
            Name "Push"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag_push
            #include "UnityCG.cginc"
            sampler2D _MainTex; float4 _MainTex_TexelSize;
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
            v2f vert(appdata_img v){ v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.uv=v.texcoord; return o; }
            half4 frag_push(v2f i):SV_Target
            {
                float4 c = tex2D(_MainTex,i.uv);
                if (c.a > 0.001) return c;
                float4 acc=0; float wsum=0;
                [unroll]
                for (int y=-2;y<=2;y++) for(int x=-2;x<=2;x++)
                {
                    float d=3-max(abs(x),abs(y));
                    float4 s=tex2D(_MainTex,i.uv+float2(x,y)*_MainTex_TexelSize.xy);
                    float w=max(s.a,0.0001)*d;
                    acc += s*w; wsum+=w;
                }
                if (wsum>0.001){ float4 r=acc/wsum; r.a=0; return r; }
                return c;
            }
            ENDCG
        }
    }
}
