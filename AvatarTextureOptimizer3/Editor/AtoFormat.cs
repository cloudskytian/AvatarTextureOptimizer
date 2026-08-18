// English: Safe platform compression. Never strips required channels. NPOT drops PVRTC.
// 中文：安全的平台压缩。绝不丢掉必需通道。NPOT 时剔除 PVRTC。
using net.fosa.ato;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class AtoFormat
    {
        public static void Apply(Texture2D tex, AtoPlatform platform, AtoSafeCompression choice,
            AtoTextureClass cls, bool hasAlpha, bool linear, bool npot, bool mipStreaming)
        {
            if (tex == null) return;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = tex.filterMode == FilterMode.Point ? FilterMode.Point : FilterMode.Bilinear;

            if (choice == AtoSafeCompression.Small && hasAlpha)
            {
                AtoLog.Warn($"{tex.name}: Small/no-alpha format refused because texture has alpha. Using Balanced.");
                choice = AtoSafeCompression.Balanced;
            }
            if (cls == AtoTextureClass.Gray || cls == AtoTextureClass.Mask)
            {
                if (choice == AtoSafeCompression.Small && HasMultipleUsedChannels(tex))
                {
                    AtoLog.Warn($"{tex.name}: gray requested single-channel but content is multi-channel. Keeping RGBA.");
                    ErrorReportSafe("warn.gray_multichannel", tex.name);
                    choice = AtoSafeCompression.Balanced;
                }
            }

            var fmt = Pick(platform, choice, cls, hasAlpha, npot);
            try
            {
                if (fmt != TextureFormat.RGBA32 && fmt != TextureFormat.ARGB32)
                    EditorUtility.CompressTexture(tex, fmt, TextureCompressionQuality.Normal);
            }
            catch (System.Exception e)
            {
                AtoLog.Warn($"Compress {tex.name} to {fmt} failed ({e.Message}), keep RGBA32.");
            }

            tex.Apply(mipStreaming, false);
            if (mipStreaming)
            {
                try
                {
                    var so = new SerializedObject(tex);
                    var sm = so.FindProperty("m_StreamingMipmaps");
                    if (sm != null) { sm.boolValue = true; so.ApplyModifiedPropertiesWithoutUndo(); }
                }
                catch { /* generated textures may not expose the property */ }
                tex.requestedMipmapLevel = 0;
            }
            // Force Clamp + drop CPU copy after GPU upload (atlas Read/Write off).
            tex.wrapMode = TextureWrapMode.Clamp;
            try { tex.Apply(mipStreaming, true); } catch { /* already non-readable */ }
            AtoLog.VerboseInfo($"format {tex.name} {fmt} mipStream={mipStreaming} clamp linear={linear} class={cls}");
        }

        public static TextureFormat Pick(AtoPlatform platform, AtoSafeCompression choice,
            AtoTextureClass cls, bool hasAlpha, bool npot)
        {
            if (choice == AtoSafeCompression.Uncompressed)
                return hasAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24;

            if (cls == AtoTextureClass.Normal)
            {
                if (platform == AtoPlatform.PC) return TextureFormat.BC5;
                return TextureFormat.RGBA32; // ASTC normal via RGBA then platform importer; safe fallback
            }

            if (platform == AtoPlatform.PC)
            {
                if (choice == AtoSafeCompression.HighQuality) return TextureFormat.BC7;
                if (choice == AtoSafeCompression.Small && !hasAlpha) return TextureFormat.DXT1;
                return TextureFormat.DXT5;
            }

            // Mobile ASTC. iOS NPOT: never PVRTC.
            if (platform == AtoPlatform.iOS && npot)
                AtoLog.VerboseInfo("iOS NPOT: PVRTC excluded.");

            if (choice == AtoSafeCompression.HighQuality) return TextureFormat.ASTC_6x6;
            if (choice == AtoSafeCompression.Small) return TextureFormat.ASTC_10x10;
            return TextureFormat.ASTC_8x8;
        }

        private static bool HasMultipleUsedChannels(Texture2D tex)
        {
            try
            {
                var px = tex.GetPixels32();
                bool r = false, g = false, b = false, aVar = false;
                byte a0 = px.Length > 0 ? px[0].a : (byte)255;
                for (int i = 0; i < px.Length; i += Mathf.Max(1, px.Length / 2048))
                {
                    var p = px[i];
                    if (p.r > 2 && p.r < 253) r = true;
                    if (p.g > 2 && p.g < 253) g = true;
                    if (p.b > 2 && p.b < 253) b = true;
                    if (p.a != a0) aVar = true;
                }
                int n = (r ? 1 : 0) + (g ? 1 : 0) + (b ? 1 : 0) + (aVar ? 1 : 0);
                return n > 1;
            }
            catch { return true; }
        }

        private static void ErrorReportSafe(string key, string arg)
        {
            try
            {
                nadena.dev.ndmf.ErrorReport.ReportError(AtoErrors.Localizer,
                    nadena.dev.ndmf.ErrorSeverity.NonFatal, key, arg);
            }
            catch { /* report optional */ }
        }
    }
}
