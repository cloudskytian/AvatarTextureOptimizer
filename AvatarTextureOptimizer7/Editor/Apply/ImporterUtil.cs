using System.Collections.Generic;
using Fosa.AvatarTextureOptimizer;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Applies safe compression / mip streaming. Forces Clamp and disables Read/Write on atlases.
    /// 应用安全压缩与 MipStreaming。图集强制 Clamp 并关闭 Read/Write。
    /// </summary>
    public static class ImporterUtil
    {
        public static void ApplyGenerated(AtoSession session, IEnumerable<Texture2D> textures, AtoTextureKind kind,
            bool hasAlpha, bool isAtlas)
        {
            var settings = session.PlatformSettings != null
                ? session.PlatformSettings.formats
                : new AtoKindFormatSettings();
            var chosen = PickFormat(session, settings, kind, hasAlpha);
            var unity = AtoPlatformUtil.ToUnity(chosen, hasAlpha, kind, session.Platform, session.Npot);

            if (hasAlpha && AtoPlatformUtil.IsOpaqueOnly(chosen) && chosen != AtoSafeFormat.Auto)
            {
                session.WarnNdmf("warn.alphaFormat", chosen.ToString());
                unity = AtoPlatformUtil.ToUnity(AtoSafeFormat.Auto, true, kind, session.Platform, session.Npot);
            }

            if (kind == AtoTextureKind.Gray && hasAlpha == false)
            {
                // If user picked a single-channel format but the texture is multi-channel, keep multi and warn.
                // 用户选了单通道但内容仍是多通道时，保留多通道并警告。
            }

            foreach (var tex in textures)
            {
                if (tex == null) continue;
                tex.wrapMode = isAtlas ? TextureWrapMode.Clamp : tex.wrapMode;
                if (isAtlas) tex.wrapModeU = tex.wrapModeV = TextureWrapMode.Clamp;

                var mips = settings.enableMipStreaming;
                try
                {
                    if (mips && tex.mipmapCount <= 1)
                    {
                        tex.Apply(true, false);
                    }

                    EditorUtility.CompressTexture(tex, unity, TextureCompressionQuality.Normal);
                }
                catch (System.Exception e)
                {
                    session.Log.Warn("CompressTexture " + tex.name + " -> " + unity + " failed: " + e.Message);
                }

                session.Log.VerboseInfo("Import " + tex.name + " format=" + tex.format + " mips=" + mips +
                                        " clamp=" + isAtlas);
            }
        }

        static AtoSafeFormat PickFormat(AtoSession session, AtoKindFormatSettings s, AtoTextureKind kind, bool hasAlpha)
        {
            switch (kind)
            {
                case AtoTextureKind.Normal: return s.normalFormat;
                case AtoTextureKind.Gray:
                case AtoTextureKind.Mask: return s.grayFormat;
                default:
                    return hasAlpha ? s.transparentFormat : s.opaqueFormat;
            }
        }

        public static void CountSourcePixels(AtoSession session, AtoGraph graph)
        {
            var seen = new HashSet<int>();
            foreach (var ug in graph.UvGroups)
            foreach (var t in ug.Textures)
            {
                if (t == null || !seen.Add(t.GetInstanceID())) continue;
                session.Report.SourceTextures++;
                session.Report.SourcePixels += (long)t.width * t.height;
            }
        }
    }
}
