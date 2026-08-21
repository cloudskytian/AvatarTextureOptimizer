using System;
using System.IO;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// Persist generated atlases/fallbacks so VRChat sees real TextureImporter flags
    /// (mipmaps + streamingMipmaps bound together, Clamp, no RW).
    /// Prefers a PNG next to the NDMF container; falls back to sub-asset + CompressTexture.
    /// 把生成贴图写成带 TextureImporter 的 PNG，VRChat 才能识别 MipStreaming。
    /// 失败则回退 NDMF 子资源 + CompressTexture。
    /// </summary>
    public static class AtoExport
    {
        public static Texture2D Commit(
            BuildContext ctx, Texture2D tex, AtoTextureClass cls,
            AtoResolvedSettings s, AtoReport report,
            FilterMode filter, int aniso, bool linear)
        {
            if (tex == null) return null;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = filter;
            tex.anisoLevel = Math.Max(1, aniso);

            var imported = TryImporterPng(ctx, tex, cls, s, report, filter, aniso, linear);
            if (imported != null) return imported;

            AtoFormats.CompressSafe(tex, cls, s, report);
            ctx.AssetSaver.SaveAsset(tex);
            AtoLog.Detail("export sub-asset fallback " + tex.name);
            return tex;
        }

        static Texture2D TryImporterPng(
            BuildContext ctx, Texture2D tex, AtoTextureClass cls,
            AtoResolvedSettings s, AtoReport report,
            FilterMode filter, int aniso, bool linear)
        {
            try
            {
                var container = ctx.AssetSaver.CurrentContainer;
                var cpath = container != null ? AssetDatabase.GetAssetPath(container) : null;
                if (string.IsNullOrEmpty(cpath)) return null;
                var dir = Path.GetDirectoryName(cpath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

                byte[] png;
                try { png = tex.EncodeToPNG(); }
                catch (Exception e)
                {
                    AtoLog.Detail("EncodeToPNG failed: " + e.Message);
                    return null;
                }
                if (png == null || png.Length == 0) return null;

                var file = Path.Combine(dir, Sanitize(tex.name) + ".png");
                file = file.Replace('\\', '/');
                File.WriteAllBytes(file, png);
                AssetDatabase.ImportAsset(file, ImportAssetOptions.ForceUpdate);

                var ti = AssetImporter.GetAtPath(file) as TextureImporter;
                if (ti == null) return null;

                bool mips = s.formats.ForClass(cls).mipAndStreaming;
                ti.textureType = cls == AtoTextureClass.Normal
                    ? TextureImporterType.NormalMap
                    : TextureImporterType.Default;
                ti.sRGBTexture = !linear && cls != AtoTextureClass.Normal && cls != AtoTextureClass.Gray;
                ti.mipmapEnabled = mips;
                ti.streamingMipmaps = mips; // VRChat: mip on ⇒ streaming on
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.filterMode = filter;
                ti.anisoLevel = Math.Max(1, aniso);
                ti.isReadable = false;
                ti.npotScale = TextureImporterNPOTScale.None;
                ti.alphaIsTransparency = cls == AtoTextureClass.Transparent;
                ti.textureCompression = TextureImporterCompression.Compressed;

                var want = s.formats.ForClass(cls).format;
                if (!AtoFormats.Allowed(want, s.platform, cls, s.experimentalNpot))
                {
                    report.Warnings.Add(tex.name + " format " + want + " illegal, Auto");
                    want = AtoSafeFormat.Auto;
                }
                if (cls == AtoTextureClass.Gray && want == AtoSafeFormat.BC4)
                {
                    // Importer-side single-channel is still unsafe if pixels are multi-channel.
                    // 导入器单通道对多通道灰度不安全，交给 RGBA。
                    want = AtoSafeFormat.RGBA32;
                    report.Warnings.Add(tex.name + " gray forced off BC4 at import");
                }

                ApplyPlatform(ti, s.platform, want, cls, Math.Max(tex.width, tex.height));
                ti.SaveAndReimport();

                var loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(file);
                if (loaded == null) return null;
                loaded.wrapMode = TextureWrapMode.Clamp;
                ObjectRegistry.RegisterReplacedObject(tex, loaded);
                AtoLog.Info("export PNG+importer " + file + " mips=" + mips + " stream=" + mips
                            + " type=" + cls + " " + loaded.width + "x" + loaded.height);
                return loaded;
            }
            catch (Exception e)
            {
                AtoLog.Detail("PNG importer export failed: " + e.Message);
                return null;
            }
        }

        static void ApplyPlatform(TextureImporter ti, AtoBuildPlatform p, AtoSafeFormat want, AtoTextureClass cls, int maxSide)
        {
            string plat = p == AtoBuildPlatform.Android ? "Android"
                : p == AtoBuildPlatform.iOS ? "iOS"
                : "Standalone";
            var ps = ti.GetPlatformTextureSettings(plat);
            ps.overridden = true;
            ps.maxTextureSize = Mathf.NextPowerOfTwo(Math.Max(32, maxSide));
            if (ps.maxTextureSize > 8192) ps.maxTextureSize = 8192;
            if ((p == AtoBuildPlatform.Android || p == AtoBuildPlatform.iOS) && ps.maxTextureSize > 4096)
                ps.maxTextureSize = 4096;
            ps.format = ToImporterFormat(want, cls, p);
            ps.textureCompression = TextureImporterCompression.Compressed;
            ti.SetPlatformTextureSettings(ps);
        }

        public static TextureImporterFormat ToImporterFormat(AtoSafeFormat f, AtoTextureClass c, AtoBuildPlatform p)
        {
            if (f == AtoSafeFormat.Auto) f = AtoFormats.DefaultFor(p, c);
            switch (f)
            {
                case AtoSafeFormat.RGB24: return TextureImporterFormat.RGB24;
                case AtoSafeFormat.RGBA32: return TextureImporterFormat.RGBA32;
                case AtoSafeFormat.DXT1: return TextureImporterFormat.DXT1;
                case AtoSafeFormat.DXT5: return TextureImporterFormat.DXT5;
                case AtoSafeFormat.BC4: return TextureImporterFormat.BC4;
                case AtoSafeFormat.BC5: return TextureImporterFormat.BC5;
                case AtoSafeFormat.BC7: return TextureImporterFormat.BC7;
                case AtoSafeFormat.ETC2_RGB: return TextureImporterFormat.ETC2_RGB4;
                case AtoSafeFormat.ETC2_RGBA8: return TextureImporterFormat.ETC2_RGBA8;
                case AtoSafeFormat.ASTC_4x4: return TextureImporterFormat.ASTC_4x4;
                case AtoSafeFormat.ASTC_5x5: return TextureImporterFormat.ASTC_5x5;
                case AtoSafeFormat.ASTC_6x6: return TextureImporterFormat.ASTC_6x6;
                case AtoSafeFormat.ASTC_8x8: return TextureImporterFormat.ASTC_8x8;
                default: return TextureImporterFormat.Automatic;
            }
        }

        static string Sanitize(string n)
        {
            if (string.IsNullOrEmpty(n)) return "ATO_tex";
            foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return n;
        }
    }
}
