// -----------------------------------------------------------------------------
// ATOTextureParams.cs — compression / mip / streaming application for built textures.
// ATOTextureParams.cs —— 为生成贴图应用压缩、Mip 与流式参数。
//
// - Mip & MipStreaming are ONE switch (VRChat rule: mips ⇒ streaming).
//   Mip 与流式绑定为一个开关（VRChat 规则：开Mip必开流式）。
// - Streaming on runtime textures via SerializedObject "m_StreamingMipmaps"
//   (technique verified in avatar-compressor).
//   运行时贴图开流式经 SerializedObject m_StreamingMipmaps（avatar-compressor 已验证）。
// - Read/Write stays OFF (Apply makeNoLongerReadable). Texture wrap forced Clamp.
//   Read/Write 保持关闭；wrap 强制 Clamp。
// -----------------------------------------------------------------------------

using System;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class ATOTextureParams
    {
        /// <summary>Apply params to a freshly built (still readable RGBA32) texture.
        /// 对刚生成的（仍可读的 RGBA32）贴图应用参数。</summary>
        public static void Apply(Texture2D tex, TexClass cls, ATOBuildState st, string nameHint)
        {
            var s = st.settings;
            bool mipOn = MipEnabled(cls, s);
            var (fmt, note) = ATOPlatform.Resolve(FormatChoice(cls, st), cls, s.platform,
                IsPOT(tex.width, tex.height));

            tex.wrapMode = TextureWrapMode.Clamp; // forced / 强制
            tex.filterMode = FilterMode.Bilinear;
            tex.anisoLevel = 4;

            try
            {
                tex.Apply(mipOn, false);

                if (fmt != TextureFormat.RGBA32)
                {
                    EditorUtility.CompressTexture(tex, fmt, TextureCompressionQuality.Best);
                }
            }
            catch (Exception e)
            {
                // fallback chain: platform default → RGBA32 / 兜底链
                st.report.AddWarning(
                    $"Compress '{nameHint}' → {fmt} failed ({e.Message}); fallback applied / 已兜底");
                try
                {
                    var (fb, _) = FallbackFormat(cls, s.platform);
                    if (fb != TextureFormat.RGBA32)
                        EditorUtility.CompressTexture(tex, fb, TextureCompressionQuality.Normal);
                }
                catch (Exception) { /* keep RGBA32 / 保持RGBA32 */ }
            }

            // Streaming mip / 流式 Mip（ SerializedObject 方式）
            if (mipOn)
            {
                try
                {
                    using var so = new SerializedObject(tex);
                    var prop = so.FindProperty("m_StreamingMipmaps");
                    if (prop != null)
                    {
                        prop.boolValue = true;
                        var prio = so.FindProperty("m_StreamingMipmapsPriority");
                        if (prio != null) prio.intValue = 0;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                    else
                    {
                        st.report.AddWarning(
                            $"m_StreamingMipmaps not found on '{nameHint}' (Unity version?) / 未找到流式属性");
                    }
                }
                catch (Exception e)
                {
                    st.report.AddWarning($"Streaming mip set failed for '{nameHint}': {e.Message}");
                }
            }

            ATOLog.Debug($"tex params '{nameHint}': fmt={fmt} ({note}) mip={mipOn} {tex.width}x{tex.height}");
        }

        private static ATOFormat FormatChoice(TexClass cls, ATOBuildState st)
        {
            var set = ATOPlatform.EffectiveFormats(st);
            switch (cls)
            {
                case TexClass.AlbedoAlpha: return set.albedoAlpha;
                case TexClass.NormalMap: return set.normalMap;
                case TexClass.GrayMask: return set.grayMask;
                default: return set.albedoOpaque;
            }
        }

        private static bool MipEnabled(TexClass cls, ATOBuildState st)
        {
            var m = st.settings.mips;
            switch (cls)
            {
                case TexClass.NormalMap: return m.normalMap;
                case TexClass.GrayMask: return m.grayMask;
                default: return m.albedo;
            }
        }

        private static (TextureFormat, string) FallbackFormat(TexClass cls, net.fosa.ato.ATOPlatform p)
        {
            if (p == net.fosa.ato.ATOPlatform.PC)
                return (cls == TexClass.AlbedoOpaque ? TextureFormat.DXT1 : TextureFormat.DXT5, "fb pc");
            return (TextureFormat.ASTC_6x6, "fb mobile");
        }

        internal static bool IsPOT(int w, int h) =>
            (w & (w - 1)) == 0 && (h & (h - 1)) == 0 && w >= 1 && h >= 1;

        /// <summary>Detect whether a gray mask actually uses multiple channels (spec: refuse
        /// single-channel formats then, keep multi-channel + warn).
        /// 检测灰度蒙版是否实际使用多通道（规格：此时拒绝单通道格式，保多通道并警告）。</summary>
        public static bool GrayUsesMultipleChannels(TexInfo tex, ATOBuildState st)
        {
            var buf = ATOQuality.GetBuffer(tex, st);
            if (buf == null) return false;
            bool gr = false, gg = false, gb = false, ga = false;
            var first = buf.pixels.Length > 0 ? buf.pixels[0] : default;
            int step = Mathf.Max(1, buf.pixels.Length / 8192);
            for (int i = step; i < buf.pixels.Length; i += step)
            {
                var c = buf.pixels[i];
                if (c.r != first.r) gr = true;
                if (c.g != first.g) gg = true;
                if (c.b != first.b) gb = true;
                if (c.a != first.a) ga = true;
                if ((gr ? 1 : 0) + (gg ? 1 : 0) + (gb ? 1 : 0) + (ga ? 1 : 0) > 1) return true;
            }

            return false;
        }
    }
}
