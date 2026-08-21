// TextureWriter.cs - Finalize atlas / rescaled textures: readback, safe format selection, compression,
// mipmap+streaming binding, name "ATO_" prefix. / 图集与缩放贴图定稿：回读、安全格式选择、压缩、
// Mipmap与流式绑定、ATO_前缀命名。
// Safety rules / 安全规则:
//  - alpha content never gets an alpha-less format (auto override + ndmf warning) / 有透明内容绝不落到无alpha格式
//  - multi-channel grayscale forced multi-channel format + warning / 多通道灰度强制多通道格式并警告
//  - Read/Write off, Clamp forced (not user editable) / 关闭Read/Write，强制Clamp
//  - mipmaps and MipStreaming share ONE switch (VRChat requirement) / Mipmap与MipStreaming共用一个开关
//  - iOS never gets PVRTC (not offered at all); NPOT verified OK with streaming/crunch / iOS不提供PVRTC；NPOT已验证可用
using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.ATO.Editor.Core;
using Fosa.ATO.Runtime;
using UnityEditor;
using UnityEngine;
using Fosa.ATO.Editor.Analysis;
using Fosa.ATO.Editor.Quality;

namespace Fosa.ATO.Editor.Atlas
{
    public static class TextureWriter
    {
        /// <summary>Warnings fed into the ndmf report. / 送入ndmf报告的警告。</summary>
        public static readonly List<(string key, object[] args)> Warnings = new List<(string, object[])>();

        /// <summary>Read back an atlas RT and produce the final compressed texture. / 回读图集RT并产出最终压缩贴图。</summary>
        public static Texture2D FinalizeAtlas(RenderTexture rt, AtlasImage img, ATOSettings st, ATOPlatform platform)
        {
            var catOpts = st.ForCategory(img.category);
            bool mips = catOpts.mipmapsAndStreaming;
            var tex = ReadBack(rt, img.srgb, mips);
            tex.name = "ATO_" + (img.isNormal ? "N_" : img.category == ATOTextureCategory.Grayscale ? "M_" : "A_") + img.plan.id;
            Compress(tex, img.category, catOpts.compression, platform, multiChannel: true);
            if (mips) EnableStreaming(tex);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.wrapModeU = tex.wrapModeV = TextureWrapMode.Clamp;
            tex.filterMode = img.filter;
            tex.MakeNonReadable();
            return tex;
        }

        /// <summary>Whole-texture rescale output. / 整图缩放产物。</summary>
        public static Texture2D FinalizeRescaled(GPUTexOps ops, TexEntry e, ATOSettings st, ATOPlatform platform)
        {
            var catOpts = st.ForCategory(e.Category());
            var src = ops.ToLinearRT(e.texture);
            int w = Mathf.Max(1, Mathf.RoundToInt(e.texture.width * e.wholeScale));
            int h = Mathf.Max(1, Mathf.RoundToInt(e.texture.height * e.wholeScale));
            var down = ops.Downsample(src, new RectInt(0, 0, e.texture.width, e.texture.height), w, h,
                e.IsNormal, e.Category() == ATOTextureCategory.Transparent);
            var tex = ReadBack(down, e.import.sRGB, catOpts.mipmapsAndStreaming);
            RenderTexture.ReleaseTemporary(down);
            tex.name = "ATO_R_" + e.texture.name;
            Compress(tex, e.Category(), catOpts.compression, platform, multiChannel: !e.usesColor_);
            if (catOpts.mipmapsAndStreaming) EnableStreaming(tex);
            tex.wrapMode = e.texture.wrapMode;    // rescaled textures keep their wrap / 整图缩放保持原wrap
            tex.filterMode = e.texture.filterMode;
            tex.MakeNonReadable();
            return tex;
        }

        // ------------------------------------------------------------------
        // Readback / 回读
        // ------------------------------------------------------------------

        private static Texture2D ReadBack(RenderTexture rt, bool srgb, bool mips)
        {
            int mipCount = mips ? Mathf.FloorToInt(Mathf.Log(Mathf.Max(rt.width, rt.height), 2f)) + 1 : 1;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, mipCount > 1, !srgb);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            tex.Apply(mipCount > 1);
            RenderTexture.active = prev;
            return tex;
        }

        // ------------------------------------------------------------------
        // Format selection / 格式选择
        // ------------------------------------------------------------------

        /// <summary>Pick the final TextureFormat with safety overrides. / 选择最终格式（含安全覆写）。</summary>
        public static TextureFormat SelectFormat(ATOTextureCategory cat, bool hasAlphaContent, bool multiChannel, ATOCompression user, ATOPlatform platform, out string warning)
        {
            warning = null;
            bool mobile = platform != ATOPlatform.PC;
            if (mobile)
            {
                // ASTC family only (PVRTC excluded by design) / 仅ASTC（设计上排除PVRTC）
                TextureFormat F(ATOCompression c) => c switch
                {
                    ATOCompression.ASTC_4x4 => TextureFormat.ASTC_4x4,
                    ATOCompression.ASTC_5x5 => TextureFormat.ASTC_5x5,
                    ATOCompression.ASTC_6x6 => TextureFormat.ASTC_6x6,
                    ATOCompression.ASTC_8x8 => TextureFormat.ASTC_8x8,
                    _ => AutoMobile(cat),
                };
                var f = F(user);
                if (cat == ATOTextureCategory.Transparent && f == TextureFormat.ASTC_6x6) f = TextureFormat.ASTC_5x5;
                return f;
            }
            // PC / PC平台
            TextureFormat pc = user switch
            {
                ATOCompression.BC7 => TextureFormat.BC7,
                ATOCompression.DXT5 => TextureFormat.DXT5,
                ATOCompression.DXT1 => TextureFormat.DXT1,
                ATOCompression.BC5 => TextureFormat.BC5,
                ATOCompression.BC4 => TextureFormat.BC4,
                _ => AutoPC(cat),
            };
            // safety: alpha content must keep an alpha format / 有alpha内容必须保留alpha格式
            if (cat == ATOTextureCategory.Transparent && (pc == TextureFormat.DXT1 || pc == TextureFormat.BC4 || pc == TextureFormat.BC5))
            {
                pc = TextureFormat.BC7;
                warning = "ato.warn.fmt_alpha";
            }
            // multi-channel grayscale must stay multi-channel / 多通道灰度必须多通道
            if (cat == ATOTextureCategory.Grayscale && multiChannel && pc == TextureFormat.BC4)
            {
                pc = TextureFormat.BC7;
                warning = "ato.warn.fmt_gray";
            }
            if (cat == ATOTextureCategory.NormalMap && (pc == TextureFormat.DXT1 || pc == TextureFormat.BC4))
            {
                pc = TextureFormat.BC5;
                warning = "ato.warn.fmt_normal";
            }
            return pc;
        }

        private static TextureFormat AutoPC(ATOTextureCategory cat) => cat switch
        {
            ATOTextureCategory.Transparent => TextureFormat.BC7,
            ATOTextureCategory.NormalMap => TextureFormat.BC5,
            ATOTextureCategory.Grayscale => TextureFormat.BC4,
            _ => TextureFormat.DXT1,
        };

        private static TextureFormat AutoMobile(ATOTextureCategory cat) => cat switch
        {
            ATOTextureCategory.Transparent => TextureFormat.ASTC_5x5,
            ATOTextureCategory.NormalMap => TextureFormat.ASTC_5x5,
            _ => TextureFormat.ASTC_6x6,
        };

        /// <summary>Is a compression choice valid on the platform (for UI filtering)? / 压缩选项在平台是否有效（UI过滤用）。</summary>
        public static bool ValidOnPlatform(ATOCompression c, ATOPlatform p)
        {
            if (p == ATOPlatform.PC) return c == ATOCompression.Auto || c == ATOCompression.BC7 || c == ATOCompression.DXT5
                || c == ATOCompression.DXT1 || c == ATOCompression.BC5 || c == ATOCompression.BC4;
            return c == ATOCompression.Auto || (c >= ATOCompression.ASTC_4x4 && c <= ATOCompression.ASTC_8x8);
        }

        private static void Compress(Texture2D tex, ATOTextureCategory cat, ATOCompression user, ATOPlatform platform, bool multiChannel)
        {
            var fmt = SelectFormat(cat, true, multiChannel, user, platform, out string warning);
            try
            {
                EditorUtility.CompressTexture(tex, fmt, TextureCompressionQuality.Normal);
            }
            catch (Exception e)
            {
                ATOLog.Warn($"compress failed, keeping RGBA32 / 压缩失败，保留RGBA32: {tex.name}: {e.Message}");
                if (warning != null) Warnings.Add((warning, new object[] { tex.name }));
            }
            if (warning != null) Warnings.Add((warning, new object[] { tex.name }));
        }

        /// <summary>Enable MipStreaming via serialized property (editor assets only). / 通过序列化属性开启MipStreaming。</summary>
        public static void EnableStreaming(Texture2D tex)
        {
            try
            {
                var so = new SerializedObject(tex);
                var p = so.FindProperty("m_StreamingMipmaps");
                if (p != null) { p.boolValue = true; so.ApplyModifiedPropertiesWithoutUndo(); }
            }
            catch (Exception e) { ATOLog.Detail("streaming set failed / 开启流式失败: " + e.Message); }
        }
    }
}
