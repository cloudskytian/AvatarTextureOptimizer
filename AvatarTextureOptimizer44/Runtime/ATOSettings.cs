// ATOSettings.cs - All user-tunable optimization settings + platform overrides. / 全部可调优化设置与平台Override。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.ATO.Runtime
{
    /// <summary>Per-texture-category import options (compression & mipmap/streaming). / 按贴图分类的导入选项（压缩与Mipmap/流式加载）。</summary>
    [Serializable]
    public class ATOCategoryOptions
    {
        [Tooltip("Compression format. Auto picks the best safe format per platform & content. / 压缩格式。Auto 按平台与内容自动挑选安全最优格式。")]
        public ATOCompression compression = ATOCompression.Auto;

        [Tooltip("Generate mipmaps AND enable MipStreaming (VRChat requires streaming when mipmaps are on - one switch controls both). / 生成Mipmap并同时开启MipStreaming（VRChat要求开Mipmap必开流式——一个开关同时控制二者）。")]
        public bool mipmapsAndStreaming = true;

        public ATOCategoryOptions Clone() => (ATOCategoryOptions)MemberwiseClone();
    }

    /// <summary>All optimization parameters; shared by the global default and per-platform overrides. / 全部优化参数；全局默认与各平台Override共用同一结构。</summary>
    [Serializable]
    public class ATOSettings
    {
        // ---- Quality / 质量 ----
        [Tooltip("Quality preset. Changing it refreshes parameter values (except the Custom gear which is never overwritten). / 质量挡位。切换时刷新参数值（Custom挡不会被其他挡位覆盖）。")]
        public ATOQualityPreset qualityPreset = ATOQualityPreset.High;

        [Tooltip("Detailed thresholds of the current gear, foldable under Advanced in the UI. / 当前挡位详细阈值，UI中折叠于高级选项。")]
        public ATOQualityParams quality = ATOQualityParams.ForPreset(ATOQualityPreset.High);

        [Tooltip("Minimum pixels per real-world meter (prevents blur). / 每真实米最小像素数（防发糊）。</summary>")]
        public ATOPixelDensity minDensity = ATOPixelDensity.Px2048;

        [Tooltip("Maximum pixels per real-world meter (prevents waste). / 每真实米最大像素数（防浪费）。")]
        public ATOPixelDensity maxDensity = ATOPixelDensity.Px4096;

        // ---- Atlas / 图集 ----
        [Tooltip("Generate atlases. Off = only whole-texture scaling + import optimization, no UV touch. / 是否生成图集。关闭则仅整图缩放与导入参数优化，不动UV。")]
        public bool generateAtlas = true;

        [Tooltip("Minimum island padding. Computed padding = max(ceil(atlasEdge/128), this). / 岛间最小边距。实际边距 = max(ceil(图集边长/128), 此值)。")]
        public ATOPadding minPadding = ATOPadding.Px4;

        [Tooltip("EXPERIMENTAL: allow non-power-of-two atlas sizes (64px steps). Verified to support MipStreaming & Crunch. / 实验：允许非2的幂图集尺寸（64px步进）。已验证支持MipStreaming与Crunch。")]
        public bool experimentalNpot = false;

        // ---- Compression categories / 压缩分类 ----
        [Tooltip("Options for opaque textures. / 不透明贴图选项。")]
        public ATOCategoryOptions opaque = new ATOCategoryOptions();
        [Tooltip("Options for textures with alpha. / 透明贴图选项。")]
        public ATOCategoryOptions transparent = new ATOCategoryOptions();
        [Tooltip("Options for normal maps. / 法线贴图选项。")]
        public ATOCategoryOptions normalMap = new ATOCategoryOptions() { compression = ATOCompression.BC5 };
        [Tooltip("Options for grayscale masks. / 灰度蒙版选项。")]
        public ATOCategoryOptions grayscale = new ATOCategoryOptions() { compression = ATOCompression.BC4 };

        // ---- Dedup switches / 去重开关 ----
        [Tooltip("Merge duplicate materials (content & params identical). / 合并重复材质（内容与参数完全相同）。")]
        public bool materialDedup = true;
        [Tooltip("Merge duplicate textures/atlases (content & params identical). / 合并重复贴图/图集（内容与参数完全相同）。")]
        public bool textureDedup = true;

        public ATOSettings Clone()
        {
            var s = (ATOSettings)MemberwiseClone();
            s.quality = quality.Clone();
            s.opaque = opaque.Clone(); s.transparent = transparent.Clone();
            s.normalMap = normalMap.Clone(); s.grayscale = grayscale.Clone();
            return s;
        }

        /// <summary>Effective options for a category. / 某分类的有效选项。</summary>
        public ATOCategoryOptions ForCategory(ATOTextureCategory c)
        {
            switch (c) { case ATOTextureCategory.Opaque: return opaque; case ATOTextureCategory.Transparent: return transparent; case ATOTextureCategory.NormalMap: return normalMap; default: return grayscale; }
        }
    }

    /// <summary>One platform override entry (mirrors Unity texture platform override UX). / 单个平台Override项（仿Unity贴图平台Override交互）。</summary>
    [Serializable]
    public class ATOPlatformOverride
    {
        [Tooltip("Enable override for this platform. / 为该平台启用Override。")]
        public bool enabled = false;
        [Tooltip("Override values. Only shown when enabled. / Override值。勾选后显示。")]
        public ATOSettings settings = new ATOSettings();
    }
}
