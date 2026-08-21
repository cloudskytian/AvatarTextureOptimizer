// ATOIslandCopy.shader - Immediate-mode island blit (rotation via vertex texcoords).
// 即时模式岛拷贝着色器（以顶点tiled坐标实现旋转）。
// Draw with GL quads; vertex texcoords are assigned by the C# caller to implement 90-degree rotation.
// 用GL四边形绘制；90度旋转由C#侧顶点纹理坐标指定。
Shader "Hidden/ATO/IslandCopy"
{
    Properties
    {
        _MainTex ("", 2D) = "white" {}
    }
    SubShader
    {
        Lighting Off
        ZTest Always
        ZWrite Off
        Cull Off
        Blend Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Unpremult;   // 1 = source premultiplied / 源为预乘

            struct appdata { float4 vertex : POSITION; float2 texcoord : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                if (_Unpremult > 0.5 && c.a > 0.0039) c.rgb /= c.a;
                return c;
            }
            ENDCG
        }
    }
}
