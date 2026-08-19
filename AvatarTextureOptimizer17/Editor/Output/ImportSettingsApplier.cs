// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Output/ImportSettingsApplier.cs — 生成贴图持久化与导入设置 / Persist & configure generated textures
//
// 需求:
//  - 压缩格式安全枚举项：按透明/不透明(按图集是否有alpha)/法线/灰度区分。
//  - 平台 override（PC/Android/iOS）影响受平台限制的参数；默认读当前构建平台。
//  - 图集默认关闭 Read/Write、强制 Clamp（不可改）；其余参数取所有贴图中质量最高的。
//  - Mipmap 与 MipStreaming 绑定（一个开关同时控制）。
//  - 安全 fallback：带透明度不提供无 alpha 选项；多通道灰度不按单通道保存（报 warning）。
//  - 取消时保留硬盘上的临时资产；成功时保留成品资产。
// ============================================================================
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 生成贴图描述 / A generated texture to persist.
    /// </summary>
    public sealed class GeneratedTexture
    {
        public Texture2D texture;
        public TextureCategory category;
        public bool hasAlpha;        // 图集级是否含 alpha / atlas-level alpha
        public bool sRGB;
        public FilterMode filterMode = FilterMode.Bilinear;
        public int aniso = 1;
        public string label;
        public Texture2D source;     // 来源贴图（取质量最高参数用）/ source texture (for highest-quality params)
    }

    /// <summary>
    /// 导入设置应用器 / Import settings applier.
    /// </summary>
    public static class ImportSettingsApplier
    {
        /// <summary>生成资产根目录 / generated asset root</summary>
        public const string RootFolder = "Assets/ATO_Generated";

        /// <summary>
        /// 持久化并配置全部生成贴图 / Persist and configure all generated textures.
        /// </summary>
        public static Dictionary<Texture2D, Texture2D> PersistAndConfigure(
            List<GeneratedTexture> generated, string avatarName, ATOComponent cfg, ATOPlatform platform)
        {
            var result = new Dictionary<Texture2D, Texture2D>();
            if (generated.Count == 0) return result;

            var folder = $"{RootFolder}/{Sanitize(avatarName)}";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                EnsureFolder(RootFolder, folder);
            }

            // 清理同 Avatar 之前的 ATO_ 资产（本次构建成功时）/ clean previous ATO_ assets of this avatar
            CleanupFolder(folder);

            for (int i = 0; i < generated.Count; i++)
            {
                Cancel.Checkpoint();
                var g = generated[i];
                if (g.texture == null) continue;

                string path = $"{folder}/ATO_{i:D3}_{Sanitize(g.label)}.png";
                var bytes = g.texture.EncodeToPNG();
                File.WriteAllBytes(path, bytes);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex == null)
                {
                    Log.Warning($"Failed to import generated texture '{path}'");
                    continue;
                }

                ApplyImporter(path, tex, g, cfg, platform);
                result[g.texture] = tex;
            }

            return result;
        }

        private static void ApplyImporter(string path, Texture2D tex, GeneratedTexture g, ATOComponent cfg,
            ATOPlatform platform)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;

            // ---- 基础设置（强制项） / base settings (forced) ----
            importer.textureType = TextureImporterType.Default;   // 法线也按 Default 保持原始字节（绝不重算）/
                                                                  // normals stay Default to preserve raw bytes (never recompute)
            importer.sRGBTexture = g.sRGB;
            importer.wrapMode = TextureWrapMode.Clamp;            // 强制 Clamp / forced
            importer.isReadable = false;                          // 强制关 Read/Write / forced off
            importer.filterMode = g.filterMode;
            importer.anisoLevel = Mathf.Max(1, g.aniso);
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.alphaIsTransparency = g.hasAlpha;

            // Mipmap 与 MipStreaming 绑定 / mipmaps bound to streaming
            var categorySettings = CategorySettings(cfg, g.category, platform);
            bool mipmaps = categorySettings != null ? categorySettings.mipmaps : true;
            importer.mipmapEnabled = mipmaps;
            // MipStreaming 通过 SerializedObject 设置（参考 AAO 实测实现: m_StreamingMipmaps）/
            // MipStreaming via SerializedObject (battle-tested approach, cf. AAO: m_StreamingMipmaps)
            using (var so = new SerializedObject(tex))
            {
                var streaming = so.FindProperty("m_StreamingMipmaps");
                if (streaming != null) streaming.boolValue = mipmaps;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            // ---- 压缩格式（安全枚举 + 平台过滤 + fallback） / compression (safe enum + platform filter + fallback) ----
            var fmt = categorySettings != null ? categorySettings.format : ATOCompressionFormat.Automatic;
            bool npot = cfg.experimentalNpot;
            var resolved = ResolveFormat(fmt, g, platform, npot, out var fallbackWarning);
            if (fallbackWarning != null) Log.Warning(fallbackWarning);

            var settings = importer.GetPlatformTextureSettings(BuildTargetName(platform));
            settings.overridden = resolved != null || categorySettings?.maxSize > 0;
            if (resolved != null) settings.format = resolved.Value;
            int maxSize = categorySettings != null && categorySettings.maxSize > 0
                ? categorySettings.maxSize
                : Mathf.NextPowerOfTwo(Mathf.Max(tex.width, tex.height));
            settings.maxTextureSize = Mathf.Min(maxSize, Mathf.Max(tex.width, tex.height));
            if (cfg.crunch && CrunchSupported(resolved)) settings.crunchedCompression = true;
            importer.SetPlatformTextureSettings(settings);

            // 平台 override 勾选时：其余平台也按各自配置覆盖 /
            // when platform overrides enabled: apply each platform's override
            if (cfg.platformOverrideEnabled)
            {
                foreach (var p in new[] { ATOPlatform.PC, ATOPlatform.Android, ATOPlatform.iOS })
                {
                    if (p == platform) continue;
                    var pc = CategorySettings(cfg, g.category, p);
                    if (pc == null) continue;
                    var ps = importer.GetPlatformTextureSettings(BuildTargetName(p));
                    var pf = ResolveFormat(pc.format, g, p, npot, out _);
                    ps.overridden = pf != null || pc.maxSize > 0;
                    if (pf != null) ps.format = pf.Value;
                    if (pc.maxSize > 0) ps.maxTextureSize = pc.maxSize;
                    ps.crunchedCompression = cfg.crunch && CrunchSupported(pf);
                    importer.SetPlatformTextureSettings(ps);
                }
            }

            importer.SaveAndReimport();
        }

        private static CategoryImportSettings CategorySettings(ATOComponent cfg, TextureCategory category,
            ATOPlatform platform)
        {
            return cfg.ImportFor(category, platform);
        }

        /// <summary>
        /// 格式解析（平台过滤 + 类别安全约束 + fallback 警告）/
        /// Resolve format with platform filtering, category safety and fallback warnings.
        /// </summary>
        private static TextureImporterFormat? ResolveFormat(ATOCompressionFormat fmt, GeneratedTexture g,
            ATOPlatform platform, bool npot, out string warning)
        {
            warning = null;

            if (fmt == ATOCompressionFormat.Automatic) return null;

            // 平台允许列表 / per-platform allowed formats
            switch (platform)
            {
                case ATOPlatform.iOS:
                case ATOPlatform.Android:
                    if (IsPcOnly(fmt))
                    {
                        warning = $"[{g.label}] format {fmt} is not supported on {platform}; falling back to ASTC 6x6.";
                        return TextureImporterFormat.ASTC_6x6;
                    }
                    break;
                default:
                    break;
            }

            // 类别安全约束 / category safety
            switch (g.category)
            {
                case TextureCategory.Transparent:
                    if (!HasAlphaFormat(fmt))
                    {
                        warning = $"[{g.label}] format {fmt} has no alpha channel but texture uses alpha; falling back to ASTC 6x6 / BC7.";
                        return platform == ATOPlatform.PC ? (TextureImporterFormat?)TextureImporterFormat.BC7 : TextureImporterFormat.ASTC_6x6;
                    }
                    break;
                case TextureCategory.Grayscale:
                    if (IsSingleChannel(fmt))
                    {
                        if (UsesMultipleChannels(g))
                        {
                            warning = $"[{g.label}] grayscale set to single-channel format but texture has multi-channel data; saving multi-channel.";
                            return platform == ATOPlatform.PC ? (TextureImporterFormat?)TextureImporterFormat.BC7 : TextureImporterFormat.ASTC_6x6;
                        }
                    }
                    break;
                case TextureCategory.Normal:
                    if (fmt == ATOCompressionFormat.DXT1 || fmt == ATOCompressionFormat.BC4 || fmt == ATOCompressionFormat.RGB24)
                    {
                        warning = $"[{g.label}] format {fmt} is not suitable for normal maps; falling back to BC5 / ASTC 6x6.";
                        return platform == ATOPlatform.PC ? (TextureImporterFormat?)TextureImporterFormat.BC5 : TextureImporterFormat.ASTC_6x6;
                    }
                    break;
                default:
                    if (fmt == ATOCompressionFormat.BC4)
                    {
                        warning = $"[{g.label}] BC4 is single-channel; falling back to BC7.";
                        return platform == ATOPlatform.PC ? (TextureImporterFormat?)TextureImporterFormat.BC7 : TextureImporterFormat.ASTC_6x6;
                    }
                    break;
            }

            return ToImporterFormat(fmt);
        }

        private static bool IsPcOnly(ATOCompressionFormat f)
        {
            return f == ATOCompressionFormat.BC7 || f == ATOCompressionFormat.BC5 || f == ATOCompressionFormat.BC4 ||
                   f == ATOCompressionFormat.DXT5 || f == ATOCompressionFormat.DXT1;
        }

        private static bool HasAlphaFormat(ATOCompressionFormat f)
        {
            switch (f)
            {
                case ATOCompressionFormat.BC7:
                case ATOCompressionFormat.DXT5:
                case ATOCompressionFormat.ASTC4x4:
                case ATOCompressionFormat.ASTC6x6:
                case ATOCompressionFormat.ASTC8x8:
                case ATOCompressionFormat.ETC2RGBA8:
                case ATOCompressionFormat.RGBA32:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsSingleChannel(ATOCompressionFormat f)
        {
            return f == ATOCompressionFormat.BC4;
        }

        private static bool CrunchSupported(TextureImporterFormat? f)
        {
            return f == TextureImporterFormat.DXT5 || f == TextureImporterFormat.DXT1 ||
                   f == TextureImporterFormat.ETC2_RGBA8 || f == TextureImporterFormat.ETC2_RGB4;
        }

        private static bool UsesMultipleChannels(GeneratedTexture g)
        {
            if (g.texture == null) return false;
            var pixels = g.texture.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                if (p.r != p.g || p.r != p.b) return true;
            }
            return false;
        }

        private static TextureImporterFormat ToImporterFormat(ATOCompressionFormat f)
        {
            switch (f)
            {
                case ATOCompressionFormat.BC7: return TextureImporterFormat.BC7;
                case ATOCompressionFormat.BC5: return TextureImporterFormat.BC5;
                case ATOCompressionFormat.BC4: return TextureImporterFormat.BC4;
                case ATOCompressionFormat.DXT5: return TextureImporterFormat.DXT5;
                case ATOCompressionFormat.DXT1: return TextureImporterFormat.DXT1;
                case ATOCompressionFormat.ASTC4x4: return TextureImporterFormat.ASTC_4x4;
                case ATOCompressionFormat.ASTC6x6: return TextureImporterFormat.ASTC_6x6;
                case ATOCompressionFormat.ASTC8x8: return TextureImporterFormat.ASTC_8x8;
                case ATOCompressionFormat.ETC2RGBA8: return TextureImporterFormat.ETC2_RGBA8;
                case ATOCompressionFormat.ETC2RGB4: return TextureImporterFormat.ETC2_RGB4;
                case ATOCompressionFormat.RGBA32: return TextureImporterFormat.RGBA32;
                case ATOCompressionFormat.RGB24: return TextureImporterFormat.RGB24;
                default: return TextureImporterFormat.Automatic;
            }
        }

        private static string BuildTargetName(ATOPlatform p)
        {
            switch (p)
            {
                case ATOPlatform.Android: return "Android";
                case ATOPlatform.iOS: return "iPhone";
                default: return "Standalone";
            }
        }

        private static string Sanitize(string s)
        {
            var chars = (s ?? "").ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
                {
                    chars[i] = '_';
                }
            }
            return new string(chars);
        }

        private static void EnsureFolder(string root, string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parent = root;
            var child = folder.Substring(root.Length + 1);
            if (!AssetDatabase.IsValidFolder(parent))
            {
                AssetDatabase.CreateFolder("Assets", "ATO_Generated");
            }
            AssetDatabase.CreateFolder(parent, child);
        }

        /// <summary>清理该 Avatar 目录下旧的 ATO_ 资产 / clean old ATO_ assets for this avatar</summary>
        private static void CleanupFolder(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return;
            var assets = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
            foreach (var guid in assets)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path).StartsWith("ATO_", System.StringComparison.Ordinal))
                {
                    AssetDatabase.DeleteAsset(path);
                }
            }
        }
    }
}
