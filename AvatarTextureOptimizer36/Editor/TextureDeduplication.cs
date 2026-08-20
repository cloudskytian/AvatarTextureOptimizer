using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Fosa.AvatarTextureOptimizer;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Deduplicates equal pixel content plus equal import settings only inside the build clone. / 仅在构建克隆内按像素内容和导入设置去重。
    /// </summary>
    internal static class TextureDeduplication
    {
        public static void Apply(BuildSnapshot snapshot, ATOBuildSession.BuildContextAdapter context,
            AvatarTextureOptimizer component, ATOLogger logger, ATOBuildReport report)
        {
            if (!component.enableSourceDeduplication || snapshot.Textures.Count < 2) return;
            Dictionary<string, TextureAssetInfo> canonicalByHash = new Dictionary<string, TextureAssetInfo>();
            List<TextureAssetInfo> original = new List<TextureAssetInfo>(snapshot.Textures);

            for (int i = 0; i < original.Count; i++)
            {
                TextureAssetInfo candidate = original[i];
                if (candidate == null || candidate.Source == null) continue;
                string contentHash = Hash(candidate, snapshot, logger);
                if (string.IsNullOrEmpty(contentHash)) continue;
                string key = candidate.Fingerprint.GetHashCode() + ":" + contentHash;
                TextureAssetInfo canonical;
                if (!canonicalByHash.TryGetValue(key, out canonical))
                {
                    canonicalByHash.Add(key, candidate);
                    continue;
                }
                if (!candidate.Fingerprint.Equals(canonical.Fingerprint)) continue;

                bool mergedWhitelist = candidate.IsWhitelisted || canonical.IsWhitelisted;
                if (mergedWhitelist) canonical.IsWhitelisted = true;
                for (int referenceIndex = 0; referenceIndex < candidate.References.Count; referenceIndex++)
                {
                    TextureReference reference = candidate.References[referenceIndex];
                    reference.Texture = canonical;
                    if (mergedWhitelist) reference.IsWhitelisted = true;
                    canonical.References.Add(reference);
                }
                snapshot.TextureMap[candidate.Source] = canonical;
                snapshot.Textures.Remove(candidate);
                report.DeduplicatedTextures++;
                logger.Detail("Deduplicated texture " + candidate.DisplayName + " -> " + canonical.DisplayName);
            }

            TextureAssetInspector.RebuildTypeGroups(snapshot);
            if (report.DeduplicatedTextures > 0)
            {
                for (int i = 0; i < snapshot.MaterialUses.Count; i++)
                {
                    MaterialUse use = snapshot.MaterialUses[i];
                    for (int referenceIndex = 0; referenceIndex < use.References.Count; referenceIndex++)
                    {
                        if (use.References[referenceIndex].IsWhitelisted) use.SkipAtlas = true;
                    }
                }
                logger.Info("Source texture deduplication removed " + report.DeduplicatedTextures + " duplicate(s). / 源纹理去重移除了重复项。");
            }
        }

        private static string Hash(TextureAssetInfo texture, BuildSnapshot snapshot, ATOLogger logger)
        {
            TexturePixelData data = snapshot.PixelCache.Get(texture.Source, logger);
            if (data == null) return string.Empty;
            using (SHA256 sha = SHA256.Create())
            {
                string headerText = texture.Fingerprint.Width + ":" + texture.Fingerprint.Height + ":" +
                                    texture.Fingerprint.WrapMode + ":" + texture.Fingerprint.FilterMode + ":" +
                                    texture.Fingerprint.Mipmap + ":" + texture.Fingerprint.Streaming + ":" +
                                    texture.Fingerprint.SRGB + ":" + texture.Fingerprint.Compression + ":" +
                                    texture.Fingerprint.MaxSize;
                byte[] header = Encoding.UTF8.GetBytes(headerText);
                sha.TransformBlock(header, 0, header.Length, header, 0);
                byte[] pixels = new byte[data.Pixels.Length * 4];
                for (int i = 0; i < data.Pixels.Length; i++)
                {
                    Color32 color = data.Pixels[i];
                    int offset = i * 4;
                    pixels[offset] = color.r;
                    pixels[offset + 1] = color.g;
                    pixels[offset + 2] = color.b;
                    pixels[offset + 3] = color.a;
                }
                sha.TransformFinalBlock(pixels, 0, pixels.Length);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty);
            }
        }
    }
}
