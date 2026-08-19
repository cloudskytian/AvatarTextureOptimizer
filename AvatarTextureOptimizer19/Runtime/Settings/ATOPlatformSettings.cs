// English: Per-platform override block (atlas, mip, compression).
// 中文：按平台覆盖的参数块（图集 / Mip / 压缩）。
using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    [Serializable]
    public class ATOCompressionSet
    {
        [Tooltip("Opaque atlas / texture format.\n不透明图集/贴图格式。")]
        public ATOSafeFormat opaqueFormat = ATOSafeFormat.Auto;

        [Tooltip("Transparent (alpha) atlas / texture format.\n透明（含 alpha）图集/贴图格式。")]
        public ATOSafeFormat transparentFormat = ATOSafeFormat.Auto;

        [Tooltip("Normal-map format.\n法线贴图格式。")]
        public ATOSafeFormat normalFormat = ATOSafeFormat.Auto;

        [Tooltip("Gray / mask format.\n灰度/蒙版格式。")]
        public ATOSafeFormat grayFormat = ATOSafeFormat.Auto;

        public ATOCompressionSet Clone()
        {
            return new ATOCompressionSet
            {
                opaqueFormat = opaqueFormat,
                transparentFormat = transparentFormat,
                normalFormat = normalFormat,
                grayFormat = grayFormat
            };
        }
    }

    [Serializable]
    public class ATOMipStreamingSet
    {
        [Tooltip("Albedo / main color. Enabling mipmaps also enables Mip Streaming (VRChat requirement).\n主色。开启 Mipmap 时强制开启 MipStreaming（VRChat 要求）。")]
        public bool albedo = true;

        [Tooltip("Normal maps.\n法线贴图。")]
        public bool normal = true;

        [Tooltip("Mask / packed maps.\n蒙版 / 打包贴图。")]
        public bool mask = true;

        [Tooltip("Gray maps.\n灰度贴图。")]
        public bool gray = true;

        public ATOMipStreamingSet Clone()
        {
            return new ATOMipStreamingSet
            {
                albedo = albedo,
                normal = normal,
                mask = mask,
                gray = gray
            };
        }
    }

    [Serializable]
    public class ATOPlatformSettings
    {
        [Tooltip("Generate atlases. Off = scale whole textures, no unused-UV cull, no UV rearrange.\n生成图集。关闭则整图缩放，不剔除未使用 UV，不重排 UV。")]
        public bool generateAtlases = true;

        [Tooltip("Experimental NPOT atlas sizes (64 px step). Validated with MipStreaming and Crunch.\n实验性 NPOT 图集边长（64 步进）。已验证支持 MipStreaming 与 Crunch。")]
        public bool experimentalNpot = false;

        public ATOMinPadding minPadding = ATOMinPadding.Px4;

        public ATOPixelDensityStop minPixelDensity = ATOPixelDensityStop.Px2048;
        public ATOPixelDensityStop maxPixelDensity = ATOPixelDensityStop.Px4096;

        public ATOCompressionSet compression = new ATOCompressionSet();
        public ATOMipStreamingSet mipStreaming = new ATOMipStreamingSet();

        [Tooltip("Maximum atlas edge in pixels. 0 = platform default (PC 8192, mobile 4096).\n图集最大边长。0 = 平台默认（PC 8192，移动 4096）。")]
        public int maxAtlasEdgeOverride = 0;

        public ATOPlatformSettings Clone()
        {
            return new ATOPlatformSettings
            {
                generateAtlases = generateAtlases,
                experimentalNpot = experimentalNpot,
                minPadding = minPadding,
                minPixelDensity = minPixelDensity,
                maxPixelDensity = maxPixelDensity,
                compression = compression != null ? compression.Clone() : new ATOCompressionSet(),
                mipStreaming = mipStreaming != null ? mipStreaming.Clone() : new ATOMipStreamingSet(),
                maxAtlasEdgeOverride = maxAtlasEdgeOverride
            };
        }
    }
}
