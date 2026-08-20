// AvatarTextureOptimizer - TextureRegistry
// EN: Texture deduplication keyed by actual pixels + import settings; updates references so identical textures
// (with identical import settings) collapse into one. Whitelisted duplicates stay whitelisted.
// CN: 按「实际像素 + 导入设置」去重贴图并更新引用；白名单中的去重结果仍视为白名单。
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// EN: Dedup registry for Texture2D assets.
    /// CN: Texture2D 资产去重注册表。
    /// </summary>
    public sealed class TextureRegistry
    {
        private readonly Dictionary<string, Texture2D> _byKey = new Dictionary<string, Texture2D>();
        private readonly Dictionary<Texture2D, Texture2D> _byAsset = new Dictionary<Texture2D, Texture2D>();
        private readonly HashSet<Texture2D> _whitelistedResults = new HashSet<Texture2D>();

        /// <summary>
        /// EN: Registers a texture; returns the canonical (deduplicated) texture. Reference callers must use the
        /// returned texture instead of the original.
        /// CN: 注册贴图；返回规范（去重后）贴图。调用方必须改用返回值。
        /// </summary>
        public Texture2D Register(Texture2D tex, AtoBuildState state)
        {
            if (tex == null) return null;
            if (_byAsset.TryGetValue(tex, out var known)) return known;

            string key = ComputeKey(tex, state);
            if (_byKey.TryGetValue(key, out var canonical))
            {
                _byAsset[tex] = canonical;
                if (state.WhitelistedTextures.Contains(tex))
                    _whitelistedResults.Add(canonical);
                AtoLog.Detail($"Texture dedup: {tex.name} -> {canonical.name} (identical pixels & import settings)");
                return canonical;
            }
            _byKey[key] = tex;
            _byAsset[tex] = tex;
            if (state.WhitelistedTextures.Contains(tex))
                _whitelistedResults.Add(tex);
            return tex;
        }

        /// <summary>
        /// EN: Marks a texture as canonical-whitelisted (dedup of a whitelisted texture keeps the whitelist flag).
        /// CN: 将规范贴图标记为白名单（白名单贴图的去重结果保留白名单标志）。
        /// </summary>
        public bool IsWhitelistedResult(Texture2D tex) => _whitelistedResults.Contains(tex);

        /// <summary>
        /// EN: Key = pixel hash + import settings (size, format, sRGB, filter, wrap, mipmaps). Import-settings
        /// differences make two otherwise identical textures distinct (per spec).
        /// CN: 键 = 像素哈希 + 导入设置（尺寸、格式、sRGB、过滤、环绕、mipmap）。导入设置不同即视为不同。
        /// </summary>
        public static string ComputeKey(Texture2D tex, AtoBuildState state)
        {
            var sb = new System.Text.StringBuilder(96);
            sb.Append(tex.width).Append('x').Append(tex.height).Append('|');
            sb.Append(tex.format).Append('|');
            sb.Append(tex.isReadable ? 1 : 0).Append('|');
            sb.Append(tex.mipmapCount > 1 ? 1 : 0).Append('|');

            string path = AssetDatabase.GetAssetPath(tex);
            var importer = AssetDatabase.GetImporterOverride(path) as TextureImporter;
            if (importer != null)
            {
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                sb.Append(importer.sRGBTexture ? 1 : 0).Append('|');
                sb.Append((int)settings.filterMode).Append('|');
                sb.Append((int)settings.wrapMode).Append('|');
                sb.Append(settings.mipmapEnabled ? 1 : 0).Append('|');
                sb.Append((int)settings.textureType).Append('|');
            }
            else
            {
                sb.Append(tex.isDataSRGB() ? 1 : 0).Append('|');
                sb.Append((int)tex.filterMode).Append('|');
                sb.Append((int)tex.wrapMode).Append('|');
            }

            sb.Append('|');
            var decoded = state.Decoder != null ? state.Decoder.Decode(tex) : null;
            if (decoded != null)
            {
                var data = decoded.GetRawTextureData<Color32>();
                uint h1 = 2166136261u;
                uint h2 = 2246822519u;
                int step = Mathf.Max(1, data.Length / 4096); // 采样哈希控制开销
                for (int i = 0; i < data.Length; i += step)
                {
                    Color32 c = data[i];
                    uint v = (uint)(c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
                    h1 ^= v; h1 *= 16777619u;
                    h2 ^= v; h2 *= 16777619u + h1;
                }
                sb.Append(h1.ToString("x8")).Append(h2.ToString("x8"));
            }
            else
            {
                sb.Append(tex.GetInstanceID());
            }
            return sb.ToString();
        }
    }

    internal static class TextureExtensions
    {
        /// <summary>EN: Unity 2022+: Texture.isDataSRGB. Helper keeps call sites clean. / CN: Unity 2022+ 辅助方法。</summary>
        public static bool isDataSRGB(this Texture2D t)
        {
#if UNITY_2022_1_OR_NEWER
            return t.isDataSRGB;
#else
            return true;
#endif
        }
    }
}
