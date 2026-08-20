// ATOTextureWriter.cs — 贴图/图集写入器 / Texture & atlas writer.
// 说明：将合成图集与整图路径贴图保存为 PNG 并配置导入设置：
//  - 压缩格式按（透明/不透明/法线/灰度）分类与平台分别设置；构建时做安全过滤与兜底
//    （如：灰度分类中若存在多通道内容 → 强制多通道保存并在控制台警告；NPOT 剔除 PVRTC 等）
//  - 默认关闭 Read/Write、强制 Clamp（不给用户修改）；filterMode 取组内最高；mipmap 与 MipStreaming 绑定
//  - 所有不在白名单的贴图默认开启 MipStreaming
// Note: saves composed atlases and whole-texture-path textures as PNG with import settings: compression formats
// per category (transparent/opaque/normal/grayscale) and platform with build-time safety fallbacks (e.g. multi-channel
// grayscale forced to multi-channel + console warning; NPOT excludes PVRTC); Read/Write off and Clamp forced;
// filterMode = highest in the group; mipmap & MipStreaming bound; all non-whitelisted textures get MipStreaming.

using System;
using System.Collections.Generic;
using System.IO;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>贴图输出记录。/ Texture output record.</summary>
    internal sealed class ATOTextureOutput
    {
        public Texture2D source;      // 原贴图（可为 null = 新图集）/ source (null for new atlases)
        public Texture2D output;      // 输出贴图 / output texture
        public ATOTextureCategory category;
        public string name;           // 输出名（ATO_ 前缀图集）/ output name (ATO_ prefix for atlases)
        public int width;
        public int height;
        public long originalBytes;    // 原字节估算 / estimated original bytes
        public long outputBytes;      // 输出字节估算 / estimated output bytes
        public string format;         // 采用的格式（报告）/ applied format (for reporting)
        public bool streaming;        // MipStreaming
    }

    /// <summary>贴图/图集写入器。/ Texture & atlas writer.</summary>
    internal sealed class ATOTextureWriter
    {
        private readonly BuildContext _context;
        private readonly ATOConfig _config;
        private string _tempDir;

        public List<ATOTextureOutput> Outputs { get; } = new List<ATOTextureOutput>();

        public ATOTextureWriter(BuildContext context, ATOConfig config)
        {
            _context = context;
            _config = config;
        }

        /// <summary>获取（或创建）NDMF 临时资产目录。/ Get (or create) the NDMF temp asset folder.</summary>
        private string EnsureTempDir()
        {
            if (!string.IsNullOrEmpty(_tempDir)) return _tempDir;
            var containerPath = AssetDatabase.GetAssetPath(_context.AssetContainer);
            if (string.IsNullOrEmpty(containerPath))
            {
                // 兜底：项目 Temp 目录（测试/无容器场景）/ fallback: project Temp folder (tests / no container)
                _tempDir = "Assets/_ATO_Temp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            }
            else
            {
                _tempDir = Path.GetDirectoryName(containerPath);
            }
            if (!Directory.Exists(_tempDir)) Directory.CreateDirectory(_tempDir);
            return _tempDir;
        }

        /// <summary>
        /// 写入一张图集（linear float4 → PNG → 导入设置）。
        /// Write one atlas (linear float4 → PNG → import settings).
        /// </summary>
        public ATOTextureOutput WriteAtlas(ATOComposedAtlas atlas, string atlasName, bool isSRGB,
            FilterMode filterMode, bool hasAlpha, ATOPlatform platform, ATOPlatformConfig platformCfg)
        {
            var category = atlas.role == ATORole.Normal ? ATOTextureCategory.Normal
                : atlas.role == ATORole.Mask ? ATOTextureCategory.Grayscale
                : hasAlpha ? ATOTextureCategory.Transparent : ATOTextureCategory.Opaque;
            var name = "ATO_" + atlasName + (atlas.role == ATORole.Normal ? "_N" : atlas.role == ATORole.Mask ? "_M" : "");

            var output = WriteTexture(atlas.pixels, atlas.width, atlas.height, name,
                isSRGB, filterMode, category, platform, platformCfg, null);
            Outputs.Add(output);
            return output;
        }

        /// <summary>检查灰度贴图是否实际含多通道内容 → 由 WriteTexture 内部检查。/ Multi-channel check happens inside WriteTexture.</summary>

        /// <summary>
        /// 通用贴图写入：像素 → Texture2D → PNG → 导入设置。
        /// Generic texture write: pixels → Texture2D → PNG → import settings.
        /// </summary>
        public ATOTextureOutput WriteTexture(Unity.Collections.NativeArray<Unity.Mathematics.float4> pixels, int width, int height, string name,
            bool isSRGB, FilterMode filterMode, ATOTextureCategory category, ATOPlatform platform,
            ATOPlatformConfig platformCfg, Texture2D source)
        {
            var encodeNormal = category == ATOTextureCategory.Normal;
            var colors = ATOIslandCrop.LinearToColor32(pixels, isSRGB, encodeNormal);

            // 灰度多通道兜底：若存在 G/B/A 内容 → 强制多通道 + 警告 / grayscale multi-channel fallback
            if (category == ATOTextureCategory.Grayscale && HasNonRedContent(colors))
            {
                ATOLog.Warning($"Grayscale texture '{name}' contains multi-channel content; saving as multi-channel. (灰度贴图存在多通道内容，按多通道保存)");
                category = ATOTextureCategory.Transparent; // 含 alpha → 用带 alpha 的多通道格式 / with alpha → multi-channel format with alpha
            }

            // 透明检测：alpha < 255 → 透明 / transparency: alpha < 255 → transparent
            if (category == ATOTextureCategory.Opaque && HasAnyTransparency(colors))
                category = ATOTextureCategory.Transparent;

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
            };
            tex.SetPixels32(colors);
            tex.Apply(false, false);

            var output = SaveViaPng(tex, name, isSRGB, filterMode, category, platform, platformCfg, width, height, source);
            UnityEngine.Object.DestroyImmediate(tex);
            return output;
        }

        /// <summary>保存为 PNG 并配置导入设置。/ Save as PNG and configure import settings.</summary>
        private ATOTextureOutput SaveViaPng(Texture2D tex, string name, bool isSRGB, FilterMode filterMode,
            ATOTextureCategory category, ATOPlatform platform, ATOPlatformConfig platformCfg, int width, int height, Texture2D source)
        {
            var png = tex.EncodeToPNG();
            if (png == null) throw new InvalidOperationException("PNG encode failed for " + name);

            var dir = EnsureTempDir();
            var path = Path.Combine(dir, name + ".png");
            path = AssetDatabase.GenerateUniqueAssetPath(path);
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("Failed to get TextureImporter for " + path);

            // 分类 → 格式（含安全过滤）/ category → format (with safety filtering)
            var chosen = ResolveFormat(category, platform, platformCfg, width, height);
            var format = ToImporterFormat(chosen, category, isSRGB);
            if (format == TextureImporterFormat.Automatic)
            {
                importer.textureCompression = TextureImporterCompression.Compressed;
            }
            else
            {
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SetPlatformTextureSettings(BuildPlatformSettings(platform, format, width, height));
            }

            // 基础导入设置 / base import settings
            importer.textureType = category == ATOTextureCategory.Normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = isSRGB && category != ATOTextureCategory.Normal;
            importer.filterMode = filterMode;
            importer.wrapMode = TextureWrapMode.Clamp;              // 强制 Clamp（不给用户修改）/ forced Clamp
            importer.mipmapEnabled = _config.mipmapAndStreaming;    // mipmap 与 MipStreaming 绑定 / bound switch
            importer.streamingMipmaps = _config.mipmapAndStreaming; // VRChat 要求 / VRChat requirement
            importer.streamingMipmapsPriority = 0;
            importer.isReadable = false;                            // 默认关闭 Read/Write（不给用户修改）/ Read/Write off
            importer.anisoLevel = 1;
            importer.maxTextureSize = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(width, height)), 64, 8192);
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapFilter = TextureImporterMipFilter.BoxFilter;
            importer.crunchedCompression = false;
            importer.compressionQuality = 100;
            importer.SaveAndReimport();

            var loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (loaded == null) throw new InvalidOperationException("Failed to load written texture " + path);

            // 估算字节（报告用）/ estimated bytes (for reporting)
            long bytes = EstimateBytes(width, height, category, chosen, importer);
            var output = new ATOTextureOutput
            {
                source = source,
                output = loaded,
                category = category,
                name = name,
                width = width,
                height = height,
                outputBytes = bytes,
                originalBytes = source != null ? source.width * (long)source.height * 4 : 0,
                format = chosen.ToString(),
                streaming = _config.mipmapAndStreaming,
            };
            return output;
        }

        private static long EstimateBytes(int w, int h, ATOTextureCategory category, ATOCompressionFormat format, TextureImporter importer)
        {
            long px = (long)w * h;
            double bpp = 1.0; // BC1 8bpp ≈ 1B/px
            switch (format)
            {
                case ATOCompressionFormat.RGBA32:
                case ATOCompressionFormat.RGB24: bpp = 4; break;
                case ATOCompressionFormat.BC1:
                case ATOCompressionFormat.PVRTC_RGB4:
                case ATOCompressionFormat.ETC2_RGB:
                case ATOCompressionFormat.BC4: bpp = 1; break;
                case ATOCompressionFormat.BC3:
                case ATOCompressionFormat.BC5:
                case ATOCompressionFormat.ETC2_RGBA:
                case ATOCompressionFormat.PVRTC_RGBA4: bpp = 2; break;
                case ATOCompressionFormat.BC7:
                case ATOCompressionFormat.ASTC_4x4: bpp = 2; break;
                case ATOCompressionFormat.ASTC_6x6: bpp = 1.14; break;
                case ATOCompressionFormat.ASTC_8x8: bpp = 0.64; break;
                case ATOCompressionFormat.ASTC_12x12: bpp = 0.29; break;
                case ATOCompressionFormat.R8: bpp = 1; break;
            }
            return (long)(px * bpp);
        }

        /// <summary>解析分类对应格式（含安全过滤）。/ Resolve the format for a category (with safety filtering).</summary>
        public ATOCompressionFormat ResolveFormat(ATOTextureCategory category, ATOPlatform platform,
            ATOPlatformConfig platformCfg, int width, int height)
        {
            ATOCompressionFormat chosen;
            switch (category)
            {
                case ATOTextureCategory.Normal: chosen = platformCfg.normalFormat; break;
                case ATOTextureCategory.Grayscale: chosen = platformCfg.grayscaleFormat; break;
                case ATOTextureCategory.Transparent: chosen = platformCfg.transparentFormat; break;
                default: chosen = platformCfg.opaqueFormat; break;
            }

            var npot = width != height && !IsPot(width) || !IsPot(height);
            var isPvrtc = chosen == ATOCompressionFormat.PVRTC_RGB4 || chosen == ATOCompressionFormat.PVRTC_RGBA4;
            if (npot && isPvrtc)
            {
                ATOLog.Warning($"Format {chosen} unsupported for NPOT texture; falling back to Auto. (NPOT 贴图不支持 {chosen}，回退 Auto)");
                chosen = ATOCompressionFormat.Auto;
            }
            if (platform == ATOPlatform.PC && (chosen == ATOCompressionFormat.PVRTC_RGB4 || chosen == ATOCompressionFormat.PVRTC_RGBA4 ||
                chosen == ATOCompressionFormat.ETC2_RGB || chosen == ATOCompressionFormat.ETC2_RGBA ||
                chosen == ATOCompressionFormat.ASTC_4x4 || chosen == ATOCompressionFormat.ASTC_6x6 ||
                chosen == ATOCompressionFormat.ASTC_8x8 || chosen == ATOCompressionFormat.ASTC_12x12))
            {
                ATOLog.Warning($"Format {chosen} not applicable on PC; falling back to Auto. (PC 不适用 {chosen}，回退 Auto)");
                chosen = ATOCompressionFormat.Auto;
            }
            if (platform != ATOPlatform.PC && (chosen == ATOCompressionFormat.BC1 || chosen == ATOCompressionFormat.BC3 ||
                chosen == ATOCompressionFormat.BC4 || chosen == ATOCompressionFormat.BC5 || chosen == ATOCompressionFormat.BC7))
            {
                ATOLog.Warning($"Format {chosen} not applicable on {platform}; falling back to Auto. ({platform} 不适用 {chosen}，回退 Auto)");
                chosen = ATOCompressionFormat.Auto;
            }
            return chosen;
        }

        private static bool IsPot(int v) => (v & (v - 1)) == 0;

        /// <summary>ATO 格式枚举 → TextureImporterFormat。/ ATO format enum → TextureImporterFormat.</summary>
        private static TextureImporterFormat ToImporterFormat(ATOCompressionFormat f, ATOTextureCategory category, bool isSRGB)
        {
            switch (f)
            {
                case ATOCompressionFormat.Auto:
                    if (category == ATOTextureCategory.Normal) return TextureImporterFormat.Automatic;
                    return TextureImporterFormat.Automatic;
                case ATOCompressionFormat.RGBA32: return TextureImporterFormat.RGBA32;
                case ATOCompressionFormat.RGB24: return TextureImporterFormat.RGB24;
                case ATOCompressionFormat.BC1: return TextureImporterFormat.DXT1;
                case ATOCompressionFormat.BC3: return TextureImporterFormat.DXT5;
                case ATOCompressionFormat.BC4: return TextureImporterFormat.BC4;
                case ATOCompressionFormat.BC5: return TextureImporterFormat.BC5;
                case ATOCompressionFormat.BC7: return TextureImporterFormat.BC7;
                case ATOCompressionFormat.ETC2_RGB: return TextureImporterFormat.ETC2_RGB4;
                case ATOCompressionFormat.ETC2_RGBA: return TextureImporterFormat.ETC2_RGBA8;
                case ATOCompressionFormat.ASTC_4x4: return TextureImporterFormat.ASTC_4x4;
                case ATOCompressionFormat.ASTC_6x6: return TextureImporterFormat.ASTC_6x6;
                case ATOCompressionFormat.ASTC_8x8: return TextureImporterFormat.ASTC_8x8;
                case ATOCompressionFormat.ASTC_12x12: return TextureImporterFormat.ASTC_12x12;
                case ATOCompressionFormat.PVRTC_RGB4: return TextureImporterFormat.PVRTC_RGB4;
                case ATOCompressionFormat.PVRTC_RGBA4: return TextureImporterFormat.PVRTC_RGBA4;
                case ATOCompressionFormat.R8: return TextureImporterFormat.R8;
                default: return TextureImporterFormat.Automatic;
            }
        }

        private static TextureImporterPlatformSettings BuildPlatformSettings(ATOPlatform platform, TextureImporterFormat format, int w, int h)
        {
            var settings = new TextureImporterPlatformSettings
            {
                format = format,
                overridden = true,
                maxTextureSize = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(w, h)), 64, 8192),
                textureCompression = TextureImporterCompression.CompressedHQ,
            };
            switch (platform)
            {
                case ATOPlatform.Android: settings.name = "Android"; break;
                case ATOPlatform.iOS: settings.name = "iPhone"; break;
                default: settings.name = "Standalone"; break;
            }
            return settings;
        }

        private static bool HasAnyTransparency(Color32[] colors)
        {
            foreach (var c in colors)
                if (c.a < 255) return true;
            return false;
        }

        private static bool HasNonRedContent(Color32[] colors)
        {
            foreach (var c in colors)
                if (c.g != 0 || c.b != 0 || c.a != 0) return true;
            return false;
        }
    }
}
