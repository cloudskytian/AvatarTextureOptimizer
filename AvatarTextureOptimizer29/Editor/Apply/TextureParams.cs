// Texture parameter & compression stage: safe formats per (platform, category, alpha),
// mip+mip-streaming bound to one switch, forced Clamp, Read/Write off, safe fallbacks
// so no option combination can corrupt materials (spec).
// 贴图参数与压缩阶段：按（平台,类别,alpha）安全格式；Mipmap与MipStreaming单开关绑定；
// 强制Clamp；关闭Read/Write；任何选项组合都有安全回退（需求书）。
//
// Normal maps are channel-prepacked before compression (BC5=RG, DXTnm=AG; see
// docs/ThirdPartyNotes.md - CompressTexture does NOT swizzle).
// 法线压缩前手动通道预排列（BC5=RG，DXTnm=AG；CompressTexture 不做转换）。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato.editor
{
    internal static class TextureParams
    {
        internal static void Apply(AtoSession s)
        {
            using var _ = ATOLog.Scope("TextureParams");

            var done = new HashSet<Texture2D>();
            foreach (var kv in MaterialPatcher.Replacement)
                ApplyOne(s, kv.Value, done);
        }

        private static void ApplyOne(AtoSession s, Texture2D tex, HashSet<Texture2D> done)
        {
            if (tex == null || !done.Add(tex)) return;

            var cat = CategoryOf(s, tex);
            var cfg = s.settings.GetCategory(cat);
            TextureFormat format = ResolveFormat(s, tex, cat, cfg.format);
            bool mips = cfg.mipsAndStreaming;

            try
            {
                if (cat == AtoTexCategory.Normal) PrepackNormal(tex, format);

                EditorUtility.CompressTexture(tex, format,
                    format.ToString().Contains("Crunched") ? TextureCompressionQuality.Normal : TextureCompressionQuality.Best);
            }
            catch (System.Exception e)
            {
                var fallback = s.platform == AtoPlatform.PC ? TextureFormat.DXT5 : TextureFormat.ASTC_6x6;
                ATOLog.Warn($"compression of {tex.name} to {format} failed ({e.Message}); fallback {fallback}");
                try
                {
                    if (cat == AtoTexCategory.Normal) PrepackNormal(tex, fallback);
                    EditorUtility.CompressTexture(tex, fallback, TextureCompressionQuality.Normal);
                }
                catch (System.Exception e2)
                {
                    ATOLog.Warn($"fallback compression also failed for {tex.name}: {e2.Message}");
                }
            }

            // forced params / 强制参数
            tex.wrapMode = TextureWrapMode.Clamp;

            using var so = new SerializedObject(tex);
            var readable = so.FindProperty("m_IsReadable");
            if (readable != null) readable.boolValue = false; // Read/Write off / 关闭读写
            // mips & streaming bound: mips on -> streaming on; mips off -> streaming off
            // Mipmap 与流式绑定：开mip必开流式；关mip必关流式
            var streaming = so.FindProperty("m_StreamingMipmaps");
            if (streaming != null) streaming.boolValue = mips;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        internal static AtoTexCategory CategoryOf(AtoSession s, Texture2D tex)
        {
            foreach (var ti in s.texInfos.Values)
                if (ti.dedupTarget == tex || MaterialPatcher.Replacement.TryGetValue(ti.texture, out var r) && r == tex)
                    return ti.category;
            return tex.name.Contains("Normal") || tex.name.Contains("_normal_")
                ? AtoTexCategory.Normal : AtoTexCategory.Opaque;
        }

        internal static TextureFormat ResolveFormat(AtoSession s, Texture2D tex, AtoTexCategory cat,
            AtoTexFormat user)
        {
            bool alpha = HasAlpha(tex);

            if (s.platform == AtoPlatform.PC)
            {
                var f = user switch
                {
                    AtoTexFormat.DXT1 => TextureFormat.DXT1,
                    AtoTexFormat.DXT5 => TextureFormat.DXT5,
                    AtoTexFormat.BC7 => TextureFormat.BC7,
                    AtoTexFormat.DXT1Crunched => TextureFormat.DXT1Crunched,
                    AtoTexFormat.DXT5Crunched => TextureFormat.DXT5Crunched,
                    _ => AutoPc(cat, alpha),
                };
                return SafePc(s, tex, f, cat, alpha);
            }

            // Android / iOS: ASTC family (PVRTC never offered; NPOT-safe per spec)
            // 安卓/iOS：ASTC 系列（不提供PVRTC；NPOT安全）
            var a = user switch
            {
                AtoTexFormat.ASTC_4x4 => TextureFormat.ASTC_4x4,
                AtoTexFormat.ASTC_5x5 => TextureFormat.ASTC_5x5,
                AtoTexFormat.ASTC_6x6 => TextureFormat.ASTC_6x6,
                AtoTexFormat.ASTC_8x8 => TextureFormat.ASTC_8x8,
                _ => cat == AtoTexCategory.Normal ? TextureFormat.ASTC_5x5 : TextureFormat.ASTC_6x6,
            };
            return a;
        }

        private static TextureFormat AutoPc(AtoTexCategory cat, bool alpha)
        {
            switch (cat)
            {
                case AtoTexCategory.Normal: return TextureFormat.BC7;
                case AtoTexCategory.Gray: return TextureFormat.DXT1;
                default: return alpha ? TextureFormat.DXT5 : TextureFormat.DXT1;
            }
        }

        private static TextureFormat SafePc(AtoSession s, Texture2D tex, TextureFormat f, AtoTexCategory cat,
            bool alpha)
        {
            bool hasAlphaChannel = f == TextureFormat.DXT5 || f == TextureFormat.BC7 || f == TextureFormat.DXT5Crunched;
            if (alpha && !hasAlphaChannel)
            {
                var safe = f == TextureFormat.DXT1Crunched ? TextureFormat.DXT5Crunched : TextureFormat.DXT5;
                s.warnings.Add(string.Format(ATOL10n.Get("warn.formatUnsafe"), f, tex.name));
                return safe;
            }

            if (cat == AtoTexCategory.Gray && f == TextureFormat.DXT1 && GrayIsMultiChannel(s, tex))
            {
                s.warnings.Add(string.Format(ATOL10n.Get("warn.grayMultiChannel"), tex.name));
                // DXT1 keeps all 3 channels anyway; kept with warning (spec behavior)
            }

            return f;
        }

        private static bool HasAlpha(Texture2D tex)
        {
            // page names encode kind; alpha pages detected via tex contents flag in name tag
            // 页名编码类别；此处以源信息判定（ATO_页与整图副本都带 ATO_ 前缀）
            if (tex.name.Contains("_Alpha_")) return true;
            if (tex.name.Contains("_Opaque_") || tex.name.Contains("_Normal_") || tex.name.Contains("_Gray_"))
                return false;
            try
            {
                if (!tex.isReadable) return false;
                var px = tex.GetPixels32();
                foreach (var c in px)
                    if (c.a < 252) return true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool GrayIsMultiChannel(AtoSession s, Texture2D tex)
        {
            foreach (var ti in s.texInfos.Values)
                if (MaterialPatcher.Replacement.TryGetValue(ti.texture, out var r) && r == tex)
                    return ti.contentChannels.Count > 1;
            return false;
        }

        /// <summary>Swizzle normal channels for the target format before compression.
        /// 压缩前按目标格式预排列法线通道。</summary>
        private static void PrepackNormal(Texture2D tex, TextureFormat target)
        {
            if (!tex.isReadable) return;
            var px = tex.GetPixels32();
            // ATO outputs are always RG-encoded RGBA32; imported originals keep their layout
            // ATO 产物统一 RG 编码的 RGBA32；导入原贴图保持其原布局
            var source = tex.format == TextureFormat.RGBA32 && tex.name.StartsWith("ATO_")
                ? NormalLayout.RG
                : TexturePixels.DetectLayout(tex.format);
            bool targetAg = target == TextureFormat.DXT5 || target == TextureFormat.BC7
                || target == TextureFormat.DXT5Crunched;
            bool sourceAg = source == NormalLayout.AG;

            if (targetAg == sourceAg) return; // already correct / 已正确

            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                if (targetAg) px[i] = new Color32(c.b, c.g, c.b, c.r); // RG->AG
                else px[i] = new Color32(c.a, c.g, c.b, 255);           // AG->RG
            }

            tex.SetPixels32(px);
            tex.Apply(false);
        }
    }
}
