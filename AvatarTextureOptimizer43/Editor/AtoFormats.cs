using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// Platform-safe format mapping + bake-time fallbacks.
    /// 平台安全格式映射与烘焙期回退。
    /// </summary>
    public static class AtoFormats
    {
        public static AtoBuildPlatform CurrentEditorPlatform(AtoBuildPlatform stored)
        {
            if (stored != AtoBuildPlatform.Auto) return stored;
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return AtoBuildPlatform.Android;
                case BuildTarget.iOS: return AtoBuildPlatform.iOS;
                default: return AtoBuildPlatform.PC;
            }
        }

        public static AtoSafeFormat DefaultFor(AtoBuildPlatform p, AtoTextureClass c)
        {
            if (p == AtoBuildPlatform.Android || p == AtoBuildPlatform.iOS)
            {
                switch (c)
                {
                    case AtoTextureClass.Normal: return AtoSafeFormat.ASTC_4x4;
                    case AtoTextureClass.Gray: return AtoSafeFormat.ASTC_6x6;
                    case AtoTextureClass.Transparent: return AtoSafeFormat.ASTC_6x6;
                    default: return AtoSafeFormat.ASTC_6x6;
                }
            }
            switch (c)
            {
                case AtoTextureClass.Normal: return AtoSafeFormat.BC5;
                case AtoTextureClass.Gray: return AtoSafeFormat.BC4;
                case AtoTextureClass.Transparent: return AtoSafeFormat.BC7;
                default: return AtoSafeFormat.BC7;
            }
        }

        public static bool Allowed(AtoSafeFormat f, AtoBuildPlatform p, AtoTextureClass c, bool npot)
        {
            if (f == AtoSafeFormat.Auto) return true;
            if (c == AtoTextureClass.Transparent && (f == AtoSafeFormat.DXT1 || f == AtoSafeFormat.ETC2_RGB || f == AtoSafeFormat.RGB24 || f == AtoSafeFormat.BC4 || f == AtoSafeFormat.BC5))
                return false;
            if (c == AtoTextureClass.Normal && (f == AtoSafeFormat.DXT1 || f == AtoSafeFormat.BC4 || f == AtoSafeFormat.ETC2_RGB || f == AtoSafeFormat.RGB24))
                return false;
            if (p == AtoBuildPlatform.iOS || p == AtoBuildPlatform.Android)
            {
                if (f == AtoSafeFormat.DXT1 || f == AtoSafeFormat.DXT5 || f == AtoSafeFormat.BC4 || f == AtoSafeFormat.BC5 || f == AtoSafeFormat.BC7)
                    return false;
            }
            if (p == AtoBuildPlatform.PC)
            {
                if (f == AtoSafeFormat.ETC2_RGB || f == AtoSafeFormat.ETC2_RGBA8) return false;
            }
            // PVRTC is never offered. NPOT drops PVRTC-like formats (none in our enum).
            return true;
        }

        public static TextureFormat ToUnity(AtoSafeFormat f, AtoTextureClass c, AtoBuildPlatform p)
        {
            if (f == AtoSafeFormat.Auto) f = DefaultFor(p, c);
            switch (f)
            {
                case AtoSafeFormat.RGB24: return TextureFormat.RGB24;
                case AtoSafeFormat.DXT1: return TextureFormat.DXT1;
                case AtoSafeFormat.DXT5: return TextureFormat.DXT5;
                case AtoSafeFormat.BC4: return TextureFormat.BC4;
                case AtoSafeFormat.BC5: return TextureFormat.BC5;
                case AtoSafeFormat.BC7: return TextureFormat.BC7;
                case AtoSafeFormat.ETC2_RGB: return TextureFormat.ETC2_RGB;
                case AtoSafeFormat.ETC2_RGBA8: return TextureFormat.ETC2_RGBA8;
                case AtoSafeFormat.ASTC_4x4: return TextureFormat.ASTC_4x4;
                case AtoSafeFormat.ASTC_5x5: return TextureFormat.ASTC_5x5;
                case AtoSafeFormat.ASTC_6x6: return TextureFormat.ASTC_6x6;
                case AtoSafeFormat.ASTC_8x8: return TextureFormat.ASTC_8x8;
                default: return TextureFormat.RGBA32;
            }
        }

        public static void CompressSafe(Texture2D tex, AtoTextureClass cls, AtoResolvedSettings s, AtoReport report)
        {
            var want = s.formats.ForClass(cls).format;
            if (!Allowed(want, s.platform, cls, s.experimentalNpot))
            {
                report.Warnings.Add(tex.name + " format " + want + " illegal for " + cls + "/" + s.platform + ", falling back");
                want = AtoSafeFormat.Auto;
            }
            // Multi-channel gray must not be stored as BC4. 多通道灰度禁止单通道格式。
            if (cls == AtoTextureClass.Gray && (want == AtoSafeFormat.BC4) && !IsSingleChannel(tex))
            {
                report.Warnings.Add(tex.name + " gray is multi-channel; refusing BC4, saving RGBA");
                want = AtoSafeFormat.RGBA32;
            }
            var uf = ToUnity(want, cls, s.platform);
            try
            {
                if (uf != TextureFormat.RGBA32 && uf != TextureFormat.RGB24)
                    EditorUtility.CompressTexture(tex, uf, TextureCompressionQuality.Normal);
            }
            catch (Exception e)
            {
                report.Warnings.Add("Compress " + tex.name + " to " + uf + " failed: " + e.Message + " (kept RGBA32)");
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            // Force Clamp, disable CPU read/write after compress. 强制 Clamp，压缩后关闭读写。
            try { tex.Apply(s.formats.ForClass(cls).mipAndStreaming, true); }
            catch (Exception e) { AtoLog.Detail("Apply makeNoLongerReadable: " + e.Message); }
        }

        static bool IsSingleChannel(Texture2D tex)
        {
            var px = AtoTextureUtil.ReadPixels(tex);
            bool g = false, b = false, aVar = false;
            for (int i = 0; i < px.Length; i++)
            {
                if (Mathf.Abs(px[i].g - px[i].r) > 1f / 255f) g = true;
                if (Mathf.Abs(px[i].b - px[i].r) > 1f / 255f) b = true;
                if (px[i].a < 0.999f) aVar = true;
                if (g && b) return false;
            }
            return !g && !b && !aVar;
        }

        public static FilterMode BestFilter(IEnumerable<FilterMode> modes)
        {
            var best = FilterMode.Point;
            foreach (var m in modes)
                if ((int)m > (int)best) best = m;
            return best;
        }
    }
}
