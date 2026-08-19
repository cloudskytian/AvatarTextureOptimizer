// Avatar Texture Optimizer / 头像贴图优化器
// Internal GPU passes for the quality pipeline. All passes operate in linear
// space; sRGB decode of the source is done by Unity's sampler when the project
// is linear, or explicitly here when ATO_DECODE_SRGB is enabled (gamma
// projects). Textures are only ever multiplied by their alpha (premultiply),
// resampled bilinearly (handled by Blit), re-normalized (normals) and finally
// encoded back to sRGB display space for comparison.
// 质量管线的内部 GPU pass。全部在线性空间工作；当工程为线性色彩空间时源贴图
// 的 sRGB 解码由采样器完成，伽马工程时由本着色器的 ATO_DECODE_SRGB 显式完成。
// 贴图只经历：预乘 alpha、双线性重采样（Blit 完成）、法线重归一化，最后编码回
// sRGB 显示空间用于对比。

Shader "Hidden/ATO/Quality"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _SecondTex ("Fill", 2D) = "white" {}
        _CoverageTex ("Coverage", 2D) = "black" {}
        _Cutoff ("Cutoff", Float) = 0.5
        _SrcPixelSize ("SrcPixelSize", Vector) = (0,0,0,0)
        _DstPixelSize ("DstPixelSize", Vector) = (0,0,0,0)
    }

    CGINCLUDE
    #include "UnityCG.cginc"
    #pragma multi_compile_local _ ATO_DECODE_SRGB

    sampler2D _MainTex;
    sampler2D _SecondTex;
    sampler2D _CoverageTex;
    float4 _MainTex_TexelSize;
    float _Cutoff;
    float4 _SrcPixelSize;
    float4 _DstPixelSize;

    struct appdata
    {
        float4 vertex : POSITION;
        float2 uv : TEXCOORD0;
    };

    struct v2f
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    v2f vert(appdata v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = v.uv;
        return o;
    }

    float3 AtoDecodeSrgb(float3 c)
    {
        c = saturate(c);
        float3 lo = c / 12.92;
        float3 hi = pow((c + 0.055) / 1.055, 2.4);
        return lerp(lo, hi, step(0.04045, c));
    }

    float3 AtoEncodeSrgb(float3 c)
    {
        c = max(c, 0.0);
        float3 lo = c * 12.92;
        float3 hi = 1.055 * pow(c, 1.0 / 2.4) - 0.055;
        return lerp(lo, hi, step(0.0031308, c));
    }

    // Sample with optional explicit sRGB decode (gamma projects only).
    // 采样并按需显式 sRGB 解码（仅伽马工程）。
    float4 SampleLinear(sampler2D tex, float2 uv)
    {
        float4 c = tex2D(tex, uv);
    #if ATO_DECODE_SRGB
        c.rgb = AtoDecodeSrgb(c.rgb);
    #endif
        return c;
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // Pass 0: linear copy (source already linear / normal data) / 线性拷贝
        Pass
        {
            Name "LinearCopy"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                return SampleLinear(_MainTex, i.uv);
            }
            ENDCG
        }

        // Pass 1: premultiply alpha (linear) / 预乘 alpha（线性）
        Pass
        {
            Name "Premultiply"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float4 c = SampleLinear(_MainTex, i.uv);
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }

        // Pass 2: unpremultiply then encode to sRGB display bytes / 反预乘后编码为 sRGB 显示值
        Pass
        {
            Name "UnpremultiplyEncodeSRGB"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv); // already linear premultiplied / 已是线性预乘
                // Safe unpremultiply: clamp alpha low bound and saturate result to
                // avoid white explosions on fully transparent texels.
                // 安全反预乘：钳制 alpha 下限并饱和，避免全透明纹素产生白色爆炸。
                float a = max(c.a, 1e-4);
                float3 rgb = saturate(c.rgb / a);
                return float4(AtoEncodeSrgb(rgb), c.a);
            }
            ENDCG
        }

        // Pass 3: encode linear non-alpha color to sRGB, keep alpha / 非预乘线性转 sRGB，保留 alpha
        Pass
        {
            Name "EncodeSRGB"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                return float4(AtoEncodeSrgb(c.rgb), c.a);
            }
            ENDCG
        }

        // Pass 4: unpack normal map to vector field / 法线贴图解包为向量场
        Pass
        {
            Name "UnpackNormal"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                // Unity UnpackNormalmapRGorAG: handles DXTnm/BC5 (AG swizzle) and RGB layouts.
                // Unity 的 UnpackNormalmapRGorAG：兼容 DXTnm/BC5（AG 布局）与 RGB 布局。
                float3 n = UnpackNormalmapRGorAG(c);
                return float4(n, 1);
            }
            ENDCG
        }

        // Pass 5: normalize vector field / 向量场归一化
        Pass
        {
            Name "Renormalize"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                float len = max(length(c.xyz), 1e-8);
                return float4(c.xyz / len, 1);
            }
            ENDCG
        }

        // Pass 6: fill outside-mask texels with nearest inside color (pull-push seed mask in _Cutoff unused here;
        // implemented as 1px expand iterations driven from C#) / 以最近内部颜色填充掩码外纹素（C# 驱动的 1px 外扩迭代）
        Pass
        {
            Name "Dilate"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                if (c.a > 0.0) return c;
                float2 t = _MainTex_TexelSize.xy;
                float4 sum = 0;
                float cnt = 0;
                for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    if (x == 0 && y == 0) continue;
                    float4 s = tex2D(_MainTex, i.uv + float2(x, y) * t);
                    if (s.a > 0.0) { sum += s; cnt += 1; }
                }
                if (cnt > 0.001) return float4(sum.rgb / cnt, 1);
                return c; // keep alpha=0 as "empty" / alpha=0 表示空
            }
            ENDCG
        }

        // Pass 7: combine color with pull-push fill where coverage is empty; alpha
        // always comes from the original color (transparent areas stay alpha=0).
        // Pass 7：在覆盖空白处填入 pull-push 结果；alpha 恒定取自原色
        //（透明区域保持 alpha=0）。
        Pass
        {
            Name "FillCombine"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float4 m = tex2D(_MainTex, i.uv);
                float4 f = tex2D(_SecondTex, i.uv);
                float cov = tex2D(_CoverageTex, i.uv).r;
                float3 rgb = cov > 0.5 ? m.rgb : f.rgb;
                return float4(rgb, m.a);
            }
            ENDCG
        }

        // Pass 8: exact 90-degree CW pixel permutation (point sampling).
        // Pass 8：精确 90 度顺时针像素重排（最近点采样）。
        Pass
        {
            Name "Rotate90CW"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                // dst dims = (srcH, srcW); px x' in [0,srcH), y' in [0,srcW)
                // CW mapping: src(x,y) -> dst(W'-1-y, x); inverse: x = y', y = srcW-1-x'.
                // 顺时针映射：src(x,y) -> dst(W'-1-y, x)；逆映射：x = y'，y = srcW-1-x'。
                float2 dSize = _DstPixelSize.xy;
                int x2 = (int)floor(i.uv.x * dSize.x);
                int y2 = (int)floor(i.uv.y * dSize.y);
                int srcW = (int)_SrcPixelSize.x;
                int sx = y2;
                int sy = srcW - 1 - x2;
                float2 uv = (float2(sx, sy) + 0.5) / float2(_SrcPixelSize.x, _SrcPixelSize.y);
                return tex2D(_MainTex, uv);
            }
            ENDCG
        }

        // Pass 9: pack color rgb with coverage-as-alpha / 打包：rgb=颜色，a=覆盖
        Pass
        {
            Name "PackColorCoverage"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                float cov = tex2D(_CoverageTex, i.uv).r;
                return float4(c.rgb, cov);
            }
            ENDCG
        }

        // Pass 10: combine pack (alpha-as-coverage decides original vs fill) /
        // 合成打包（alpha 作覆盖：决定取原色还是填充）
        Pass
        {
            Name "CombinePack"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float4 m = tex2D(_MainTex, i.uv);
                float4 f = tex2D(_SecondTex, i.uv);
                float3 rgb = m.a > 0.001 ? m.rgb : f.rgb;
                return float4(rgb, m.a);
            }
            ENDCG
        }

        // Pass 11: pack normal vector to [0,1] storage / 法线向量打包到 [0,1] 存储
        Pass
        {
            Name "PackNormal"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float3 n = tex2D(_MainTex, i.uv).xyz;
                float len = max(length(n), 1e-8);
                n /= len;
                return float4(n * 0.5 + 0.5, 1);
            }
            ENDCG
        }

        // Pass 12: replicate alpha into rgb (alpha extrapolation source) /
        // alpha 复制到 rgb（alpha 外推源）
        Pass
        {
            Name "AlphaToRgb"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float a = tex2D(_MainTex, i.uv).a;
                return float4(a, a, a, 1);
            }
            ENDCG
        }

        // Pass 13: resolve filled premultiplied color with EXTRAPOLATED texture
        // alpha carried in _SecondTex.r, then encode sRGB. The packed coverage
        // alpha (of _MainTex) is discarded on purpose.
        // Pass 13：用 _SecondTex.r 携带的外推贴图 alpha 解出填充后的预乘颜色，
        // 再编码 sRGB。_MainTex 的打包覆盖 alpha 被有意丢弃。
        Pass
        {
            Name "ResolveUnpremultiplySRGB"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);      // premul rgb, coverage alpha / 预乘 rgb, 覆盖 alpha
                float a = tex2D(_SecondTex, i.uv).r;   // extrapolated texture alpha / 外推贴图 alpha
                float safe = max(a, 1e-4);
                float3 rgb = saturate(c.rgb / safe);
                // Spec: RGB is extrapolated infinitely, but padding keeps
                // alpha 0 (transparent stays transparent).
                // 规格：RGB 无限外扩，但 padding 的 alpha 保持 0（透明保持透明）。
                float outA = c.a > 0.5 ? a : 0.0;
                return float4(AtoEncodeSrgb(rgb), outA);
            }
            ENDCG
        }

        // Pass 14: resolve filled (non-premultiplied) linear data with the
        // extrapolated texture alpha from _SecondTex.r (no color transform).
        // Pass 14：以 _SecondTex.r 的外推贴图 alpha 解出填充后的（非预乘）
        // 线性数据（不做颜色变换）。
        Pass
        {
            Name "ResolveLinearAlpha"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);      // linear rgb, coverage alpha / 线性 rgb, 覆盖 alpha
                float a = tex2D(_SecondTex, i.uv).r;
                // Padding keeps alpha 0 (see pass 13). / padding 的 alpha 保持 0（见 pass 13）。
                float outA = c.a > 0.5 ? a : 0.0;
                return float4(c.rgb, outA);
            }
            ENDCG
        }
    }
    Fallback Off
}
