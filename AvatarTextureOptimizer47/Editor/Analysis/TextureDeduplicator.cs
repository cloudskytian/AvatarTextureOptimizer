using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using Fosa.AvatarTextureOptimizer.Editor.Reporting;
using Fosa.AvatarTextureOptimizer.Editor.Processing;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>EN: Exact decoded-pixel plus import-setting texture deduplication. ZH: 基于解码像素与导入设置的精确贴图去重。</summary>
    internal static class TextureDeduplicator
    {
        private const int StripeHeight = 128;

        public static Dictionary<Texture2D, Texture2D> Deduplicate(BuildContext context, IEnumerable<Texture2D> source,
            HashSet<Texture2D> protectedTextures, BuildProgress progress, ResourceScope resources, AtoBuildReport report)
        {
            var textures = source.Where(x => x != null).Distinct().OrderBy(x => x.GetInstanceID()).ToList();
            var canonical = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            var replacements = new Dictionary<Texture2D, Texture2D>();
            var protectedKeys = new HashSet<string>();
            var keyByTexture = new Dictionary<Texture2D, string>();

            for (var i = 0; i < textures.Count; i++)
            {
                progress.Report("Hashing decoded textures / 对解码贴图计算哈希", i, Math.Max(1, textures.Count));
                var texture = textures[i];
                var key = ComputeKey(texture, resources);
                keyByTexture[texture] = key;
                if (protectedTextures.Contains(texture)) protectedKeys.Add(key);
                if (!canonical.TryGetValue(key, out var first)) canonical[key] = texture;
                else replacements[texture] = first;
            }

            // EN: If any duplicate is protected, the complete deduplication result is protected.
            // ZH: 只要任一重复项受保护，完整去重结果都视为受保护。
            foreach (var pair in keyByTexture)
                if (protectedKeys.Contains(pair.Value)) protectedTextures.Add(canonical[pair.Value]);

            foreach (var pair in replacements)
            {
                if (protectedTextures.Contains(pair.Key)) protectedTextures.Add(pair.Value);
                ObjectRegistry.RegisterReplacedObject(pair.Key, pair.Value);
            }

            if (replacements.Count > 0)
            {
                context.Extension<AnimatorServicesContext>().AnimationIndex.RewriteObjectCurves(obj =>
                    obj is Texture2D texture && replacements.TryGetValue(texture, out var replacement) ? replacement : obj);
                SerializedReferenceRewriter.Rewrite(context.AvatarRootObject, replacements);
            }
            report.DeduplicatedTextureCount += replacements.Count;
            report.Log($"Deduplicated {replacements.Count} texture(s) by decoded pixels and importer settings.");
            return replacements;
        }

        private static string ComputeKey(Texture2D texture, ResourceScope resources)
        {
            using (var sha = SHA256.Create())
            {
                Add(sha, Encoding.UTF8.GetBytes($"{texture.width}x{texture.height}|{ImporterFingerprint(texture)}|"));
                var width = texture.width;
                for (var y = 0; y < texture.height; y += StripeHeight)
                {
                    var height = Math.Min(StripeHeight, texture.height - y);
                    var descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBFloat, 0)
                    {
                        sRGB = false,
                        useMipMap = false,
                        autoGenerateMips = false,
                    };
                    var renderTexture = RenderTexture.GetTemporary(descriptor);
                    var previous = RenderTexture.active;
                    Texture2D readback = null;
                    try
                    {
                        renderTexture.filterMode = FilterMode.Point;
                        Graphics.Blit(texture, renderTexture,
                            new Vector2(1f, (float)height / texture.height),
                            new Vector2(0f, (float)y / texture.height));
                        RenderTexture.active = renderTexture;
                        readback = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
                        readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                        readback.Apply(false, false);
                        var bytes = readback.GetRawTextureData<byte>();
                        var managed = bytes.ToArray();
                        Add(sha, managed);
                    }
                    finally
                    {
                        RenderTexture.active = previous;
                        RenderTexture.ReleaseTemporary(renderTexture);
                        if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
                    }
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Convert.ToBase64String(sha.Hash);
            }
        }

        private static void Add(HashAlgorithm algorithm, byte[] data)
        {
            algorithm.TransformBlock(data, 0, data.Length, data, 0);
        }

        private static string ImporterFingerprint(Texture2D texture)
        {
            var path = AssetDatabase.GetAssetPath(texture);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                // EN: EditorJsonUtility captures importer fields, including platform overrides, without guessing each field.
                // ZH: EditorJsonUtility 可完整捕获含平台覆盖在内的导入器字段，无需猜测字段列表。
                return EditorJsonUtility.ToJson(importer, false);
            }
            return $"runtime:{texture.graphicsFormat}:{texture.mipmapCount}:{texture.filterMode}:" +
                   $"{texture.wrapModeU}:{texture.wrapModeV}:{texture.anisoLevel}:{texture.mipMapBias:R}:{texture.isDataSRGB}";
        }
    }
}
