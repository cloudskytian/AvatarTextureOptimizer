// ATO 图集 padding 填充：跳跃洪泛外扩（jump-flood dilation）。
// 空像素（alpha=0）拉取最大 alpha 邻居；_KeepAlphaZero=1 时保持 padding alpha=0（透明贴图）。
// ATO atlas padding fill: jump-flood dilation. Empty pixels (alpha=0) pull the max-alpha neighbor;
// _KeepAlphaZero=1 keeps padding alpha at 0 (transparent textures).
Shader "Hidden/ATO/Dilate"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Step ("Step", Float) = 1
        _KeepAlphaZero ("KeepAlphaZero", Float) = 0
    }
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
            float4 _MainTex_TexelSize;
            float _Step;
            float _KeepAlphaZero;

            fixed4 frag (v2f_img i) : SV_Target
            {
                fixed4 self = tex2D(_MainTex, i.uv);
                if (self.a > 0.0) return self;
                float2 stepPx = _MainTex_TexelSize.xy * _Step;
                fixed4 best = self;
                float bestA = self.a;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        fixed4 n = tex2D(_MainTex, i.uv + float2(dx, dy) * stepPx);
                        if (n.a > bestA) { best = n; bestA = n.a; }
                    }
                }
                if (_KeepAlphaZero > 0.5) return fixed4(best.rgb, 0.0);
                return best;
            }
            ENDCG
        }
    }
}
