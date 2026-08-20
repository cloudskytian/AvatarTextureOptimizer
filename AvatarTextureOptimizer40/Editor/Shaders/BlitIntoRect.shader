Shader "Hidden/Fosa/ATO/BlitIntoRect"
{
    // Pastes _MainTex into the pixel rectangle _DstRect (xMin,yMin,xMax,yMax) on the active RT.
    // Uses standard appdata vertex input so it works with GL immediate-mode quads (GL.Begin/GL.Vertex3)
    // under a LoadPixelMatrix. _Rotated=1 rotates the UV 90° CW (for transposed atlas placements).
    // 将 _MainTex 贴到当前 RT 的 _DstRect 像素矩形内。使用标准顶点输入，兼容 LoadPixelMatrix 下的
    // GL 立即模式四边形；_Rotated=1 时 UV 顺时针旋转 90°（用于转置放置）。
    Properties { _MainTex ("", 2D) = "" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Blend Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex; float4 _MainTex_ST;
            int _Rotated;
            struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            half4 frag(v2f i):SV_Target
            {
                float2 uv = i.uv;
                if (_Rotated) uv = float2(uv.y, 1.0 - uv.x);
                return tex2D(_MainTex, uv);
            }
            ENDCG
        }
    }
}
