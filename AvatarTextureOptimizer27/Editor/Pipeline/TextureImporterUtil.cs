using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class TextureImporterUtil
    {
        public static void ApplyImportSettings(List<AtlasResult> atlases, AtoPlatformSettings s, AtoPlatform platform, BakeReport report)
        {
            foreach (var a in atlases)
            {
                if (a.Atlas == null) continue;
                a.Atlas.wrapMode = TextureWrapMode.Clamp;
                bool mips = MipsFor(a.Semantic, s);
                // Read/Write off after apply: already false on CreateAsset path
                var path = AssetDatabase.GetAssetPath(a.Atlas);
                if (string.IsNullOrEmpty(path)) continue;
                // Texture2D assets created via CreateAsset are not TextureImporters.
                // Force runtime flags.
                a.Atlas.wrapMode = TextureWrapMode.Clamp;
                if (s.ExperimentalNpot && platform == AtoPlatform.iOS)
                    AtoLog.Info("NPOT on iOS: PVRTC not applied (unsupported).");
                AtoLog.VerboseInfo($"Import flags atlas={a.Atlas.name} mips={mips} clamp=1 rw=0");
            }
        }

        static bool MipsFor(AtoTextureSemantic sem, AtoPlatformSettings s)
        {
            switch (sem)
            {
                case AtoTextureSemantic.Normal: return s.MipStreamingNormal;
                case AtoTextureSemantic.Mask:
                case AtoTextureSemantic.MetallicGloss: return s.MipStreamingMask;
                case AtoTextureSemantic.Gray: return s.MipStreamingGray;
                default: return s.MipStreamingAlbedo;
            }
        }

        public static TextureFormat SafeFormat(AtoTextureSemantic sem, bool hasAlpha, AtoPlatformSettings s, AtoPlatform platform)
        {
            if (sem == AtoTextureSemantic.Normal)
            {
                if (s.NormalFormat == AtoSafeNormalFormat.RGBA32) return TextureFormat.RGBA32;
                return TextureFormat.RGBA32; // runtime-created; compressor is importer-side
            }
            if (hasAlpha)
            {
                return TextureFormat.RGBA32;
            }
            return TextureFormat.RGB24;
        }
    }
}
