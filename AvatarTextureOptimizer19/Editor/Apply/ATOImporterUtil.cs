// English: TextureImporter setup for generated atlases / scaled textures. Unsafe formats fall back with a warning.
// 中文：为生成的图集/缩放贴图写入 TextureImporter。不安全格式回退并警告。
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOImporterUtil
    {
        public static void Apply(ATOState state, Texture2D tex, ATOTextureSemantic semantic, bool linear,
            FilterMode filter)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;

            importer.textureType = semantic == ATOTextureSemantic.Normal
                ? TextureImporterType.NormalMap
                : TextureImporterType.Default;
            importer.sRGBTexture = !linear && semantic != ATOTextureSemantic.Normal &&
                                   semantic != ATOTextureSemantic.Gray && semantic != ATOTextureSemantic.Mask;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = filter;
            importer.isReadable = false;
            importer.mipmapEnabled = MipEnabled(state, semantic);
            importer.streamingMipmaps = importer.mipmapEnabled;
            importer.streamingMipmapsPriority = 0;
            importer.anisoLevel = MaxAniso(state, tex);

            var chosen = ChooseFormat(state, semantic, importer.DoesSourceTextureHaveAlpha());
            ApplyPlatform(importer, "DefaultTexturePlatform", chosen, state);
            if (state.Platform == ATOBuildPlatform.PC)
                ApplyPlatform(importer, "Standalone", chosen, state);
            else if (state.Platform == ATOBuildPlatform.Android)
                ApplyPlatform(importer, "Android", chosen, state);
            else if (state.Platform == ATOBuildPlatform.iOS)
                ApplyPlatform(importer, "iPhone", chosen, state);

            importer.SaveAndReimport();
        }

        private static bool MipEnabled(ATOState state, ATOTextureSemantic sem)
        {
            var m = state.Settings.mipStreaming;
            switch (sem)
            {
                case ATOTextureSemantic.Normal: return m.normal;
                case ATOTextureSemantic.Gray:
                case ATOTextureSemantic.Mask: return m.gray || m.mask;
                default: return m.albedo;
            }
        }

        private static int MaxAniso(ATOState state, Texture2D atlas)
        {
            var a = 1;
            foreach (var u in state.Uses)
            {
                if (u.Texture == null) continue;
                Texture2D mapped;
                if (state.TextureReplace.TryGetValue(u.Texture, out mapped) && mapped == atlas)
                    a = Mathf.Max(a, u.Texture.anisoLevel);
            }

            return a;
        }

        private static TextureImporterFormat ChooseFormat(ATOState state, ATOTextureSemantic sem, bool hasAlpha)
        {
            var set = state.Settings.compression;
            var user = ATOSafeFormat.Auto;
            switch (sem)
            {
                case ATOTextureSemantic.Normal: user = set.normalFormat; break;
                case ATOTextureSemantic.Gray:
                case ATOTextureSemantic.Mask: user = set.grayFormat; break;
                case ATOTextureSemantic.AlbedoTransparent: user = set.transparentFormat; break;
                default: user = hasAlpha ? set.transparentFormat : set.opaqueFormat; break;
            }

            var safe = Sanitize(state, user, sem, hasAlpha);
            if (safe != user && user != ATOSafeFormat.Auto)
            {
                ErrorReport.ReportError(ATOLoc.L, ErrorSeverity.NonFatal, "warn.formatFallback",
                    user.ToString(), sem.ToString(), safe.ToString());
                state.Report.Warnings.Add("format fallback " + user + " -> " + safe + " for " + sem);
            }

            return ToUnity(safe, state.Platform);
        }

        internal static ATOSafeFormat Sanitize(ATOState state, ATOSafeFormat fmt, ATOTextureSemantic sem, bool hasAlpha)
        {
            if (fmt == ATOSafeFormat.Auto) return DefaultFor(sem, hasAlpha, state.Platform);

            if (hasAlpha && (fmt == ATOSafeFormat.DXT1 || fmt == ATOSafeFormat.RGB24 ||
                             fmt == ATOSafeFormat.ETC2_RGB4 || fmt == ATOSafeFormat.R8 ||
                             fmt == ATOSafeFormat.Alpha8 || fmt == ATOSafeFormat.BC4))
                return DefaultFor(sem, true, state.Platform);

            if (sem == ATOTextureSemantic.Normal && (fmt == ATOSafeFormat.DXT1 || fmt == ATOSafeFormat.RGB24 ||
                                                    fmt == ATOSafeFormat.R8 || fmt == ATOSafeFormat.Alpha8 ||
                                                    fmt == ATOSafeFormat.BC4))
                return DefaultFor(sem, hasAlpha, state.Platform);

            // NPOT + iOS: PVRTC is not in our enum; nothing extra. Keep ASTC.
            if (state.Settings.experimentalNpot && state.Platform == ATOBuildPlatform.iOS)
            {
                if (fmt == ATOSafeFormat.DXT1 || fmt == ATOSafeFormat.DXT5 || fmt == ATOSafeFormat.BC4 ||
                    fmt == ATOSafeFormat.BC5 || fmt == ATOSafeFormat.BC7)
                    return DefaultFor(sem, hasAlpha, state.Platform);
            }

            return fmt;
        }

        private static ATOSafeFormat DefaultFor(ATOTextureSemantic sem, bool hasAlpha, ATOBuildPlatform plat)
        {
            if (plat == ATOBuildPlatform.Android || plat == ATOBuildPlatform.iOS)
            {
                if (sem == ATOTextureSemantic.Normal) return ATOSafeFormat.ASTC_4x4;
                return ATOSafeFormat.ASTC_6x6;
            }

            if (sem == ATOTextureSemantic.Normal) return ATOSafeFormat.BC5;
            if (sem == ATOTextureSemantic.Gray || sem == ATOTextureSemantic.Mask) return ATOSafeFormat.BC4;
            return hasAlpha ? ATOSafeFormat.DXT5 : ATOSafeFormat.DXT1;
        }

        private static TextureImporterFormat ToUnity(ATOSafeFormat fmt, ATOBuildPlatform plat)
        {
            switch (fmt)
            {
                case ATOSafeFormat.RGBA32: return TextureImporterFormat.RGBA32;
                case ATOSafeFormat.RGB24: return TextureImporterFormat.RGB24;
                case ATOSafeFormat.DXT1: return TextureImporterFormat.DXT1;
                case ATOSafeFormat.DXT5: return TextureImporterFormat.DXT5;
                case ATOSafeFormat.BC4: return TextureImporterFormat.BC4;
                case ATOSafeFormat.BC5: return TextureImporterFormat.BC5;
                case ATOSafeFormat.BC7: return TextureImporterFormat.BC7;
                case ATOSafeFormat.ETC2_RGB4: return TextureImporterFormat.ETC2_RGB4;
                case ATOSafeFormat.ETC2_RGBA8: return TextureImporterFormat.ETC2_RGBA8;
                case ATOSafeFormat.ASTC_4x4: return TextureImporterFormat.ASTC_4x4;
                case ATOSafeFormat.ASTC_5x5: return TextureImporterFormat.ASTC_5x5;
                case ATOSafeFormat.ASTC_6x6: return TextureImporterFormat.ASTC_6x6;
                case ATOSafeFormat.ASTC_8x8: return TextureImporterFormat.ASTC_8x8;
                case ATOSafeFormat.R8: return TextureImporterFormat.R8;
                case ATOSafeFormat.Alpha8: return TextureImporterFormat.Alpha8;
                default: return TextureImporterFormat.Automatic;
            }
        }

        private static void ApplyPlatform(TextureImporter importer, string name, TextureImporterFormat fmt,
            ATOState state)
        {
            var s = importer.GetPlatformTextureSettings(name);
            s.overridden = name != "DefaultTexturePlatform";
            s.format = fmt;
            s.maxTextureSize = 8192;
            s.crunchedCompression = false;
            importer.SetPlatformTextureSettings(s);
        }
    }
}
