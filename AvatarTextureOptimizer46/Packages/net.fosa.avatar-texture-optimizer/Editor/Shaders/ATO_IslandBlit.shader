// SPDX-License-Identifier: MIT
// EN: Writes one island rectangle into the atlas, optionally rotated 90 degrees, and emits coverage in
//     a separate render target channel for the pull-push stage.
// ZH: 将一个岛矩形写入图集，可选旋转 90 度，并把覆盖度输出到独立通道供 pull-push 阶段使用。
Shader "Hidden/ATO/IslandBlit"
{
    Properties
    {
        _MainTex ("Island", 2D) = "white" {}
        _ATO_Rotate ("Rotate 90", Float) = 0
        _ATO_PreserveAlpha ("Preserve alpha", Float) = 1
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _ATO_Rotate;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert (appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }
            float4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;
                // EN: 90 degree rotation is a transpose plus a flip; the packer transposes the bit mask
                //     the same way so the two always agree.
                // ZH: 旋转 90 度等于转置加翻转；装箱器以同样方式转置位掩码，因此两者始终一致。
                if (_ATO_Rotate > 0.5) uv = float2(uv.y, 1.0 - uv.x);
                return tex2D(_MainTex, uv);
            }
            ENDCG
        }
    }
}
