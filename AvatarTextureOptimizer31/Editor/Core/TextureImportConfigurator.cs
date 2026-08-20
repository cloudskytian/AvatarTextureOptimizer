// TextureImportConfigurator.cs
// Phase 12: Configures atlas and optimized texture import settings.
// Sets compression formats by category (transparent/opaque/normal/mask),
// MipStreaming/Mipmap binding, Read/Write=false, Clamp wrap mode,
// and platform-specific overrides.
// 阶段12：配置图集和优化贴图的导入设置。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Core
{
    /// <summary>
    /// Configures import settings for generated atlas textures.
    /// Sets compression, mipmap/mipstreaming, and read/write per texture category.
    /// 配置生成的图集纹理的导入设置。
    /// </summary>
    internal sealed class TextureImportConfigurator
    {
        private readonly List<TextureTypeGroup> _typeGroups;
        private readonly BuildContext _context;
        private readonly ATOComponent _component;
        private readonly AdvancedSettings _settings;
        private readonly ATOLogger _log;

        internal TextureImportConfigurator(List<TextureTypeGroup> typeGroups, BuildContext context,
            ATOComponent component, AdvancedSettings settings, ATOLogger log)
        {
            _typeGroups = typeGroups;
            _context = context;
            _component = component;
            _settings = settings;
            _log = log;
        }

        internal void Execute()
        {
            foreach (var tg in _typeGroups)
            {
                foreach (var atlas in tg.Atlases)
                {
                    ConfigureAtlas(atlas, tg);
                }
            }
        }

        private void ConfigureAtlas(GeneratedAtlas atlas, TextureTypeGroup tg)
        {
            var tex = atlas.Texture;
            if (tex == null) return;

            // Determine category
            TextureCategory category = DetermineCategory(atlas, tg);
            bool hasAlpha = category == TextureCategory.Color || category == TextureCategory.Emission;

            // Set texture import settings via TextureImporter if the asset is on disk
            var path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path) && AssetDatabase.Contains(tex))
            {
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer != null)
                {
                    ConfigureImporter(importer, category, hasAlpha);
                    importer.SaveAndReimport();
                }
            }

            // Also set runtime texture properties for non-persisted assets
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.wrapModeU = TextureWrapMode.Clamp;
            tex.wrapModeV = TextureWrapMode.Clamp;
            tex.filterMode = tg.FilterMode;

            // MipStreaming / Mipmap binding
            if (_component._textureFormats.enableMipStreaming)
            {
                tex.mipMapBias = 0;
                // MipStreaming and Mipmap are bound together per spec
                // (VRChat requires MipStreaming when Mipmap is on)
            }
        }

        private void ConfigureImporter(TextureImporter importer, TextureCategory category, bool hasAlpha)
        {
            // Force settings
            importer.isReadable = false;      // Read/Write OFF (forced)
            importer.wrapMode = TextureWrapMode.Clamp; // Force Clamp
            importer.npotScale = TextureImporterNPOTScale.None; // NPOT supported
            importer.streamingMipmaps = _component._textureFormats.enableMipStreaming;
            importer.streamingMipmapsPriority = 0;

            // Mipmap/MipStreaming binding
            importer.mipmapEnabled = _component._textureFormats.enableMipStreaming;
            importer.mipmapStreamingEnabled = _component._textureFormats.enableMipStreaming;

            // Compression by category and platform
            switch (category)
            {
                case TextureCategory.Normal:
                    importer.textureType = TextureImporterType.NormalMap;
                    SetPlatformFormats(importer, _component._textureFormats.normalFormatPC,
                        _component._textureFormats.normalFormatAndroid,
                        _component._textureFormats.normalFormatIOS);
                    break;

                case TextureCategory.Mask:
                    importer.textureType = TextureImporterType.SingleChannel;
                    SetPlatformFormats(importer, _component._textureFormats.maskFormatPC,
                        _component._textureFormats.maskFormatAndroid,
                        _component._textureFormats.maskFormatIOS);
                    break;

                default:
                    importer.textureType = hasAlpha ? TextureImporterType.Default : TextureImporterType.Default;
                    importer.alphaSource = hasAlpha ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                    if (hasAlpha)
                    {
                        SetPlatformFormats(importer, _component._textureFormats.transparentFormatPC,
                            _component._textureFormats.transparentFormatAndroid,
                            _component._textureFormats.transparentFormatIOS);
                    }
                    else
                    {
                        SetPlatformFormats(importer, _component._textureFormats.opaqueFormatPC,
                            _component._textureFormats.opaqueFormatAndroid,
                            _component._textureFormats.opaqueFormatIOS);
                    }
                    break;
            }

            // NPOT: remove unsupported compression formats
            if (_component._useNPOT)
            {
                RemoveNPOTUnsupportedFormats(importer);
            }
        }

        private void SetPlatformFormats(TextureImporter importer,
            TextureCompressionFormat pcFormat,
            TextureCompressionFormat androidFormat,
            TextureCompressionFormat iosFormat)
        {
            // PC (Standalone)
            var pcSettings = importer.GetPlatformTextureSettings("Standalone");
            pcSettings.overridden = _component._platformSettings.overridePC;
            pcSettings.format = MapFormatToImporter(pcFormat, isMobile: false);
            pcSettings.maxTextureSize = _component._platformSettings.maxAtlasSizePC;
            importer.SetPlatformTextureSettings(pcSettings);

            // Android
            var androidSettings = importer.GetPlatformTextureSettings("Android");
            androidSettings.overridden = _component._platformSettings.overrideAndroid;
            androidSettings.format = MapFormatToImporter(androidFormat, isMobile: true);
            androidSettings.maxTextureSize = _component._platformSettings.maxAtlasSizeAndroid;
            importer.SetPlatformTextureSettings(androidSettings);

            // iOS
            var iosSettings = importer.GetPlatformTextureSettings("iPhone");
            iosSettings.overridden = _component._platformSettings.overrideIOS;
            iosSettings.format = MapFormatToImporter(iosFormat, isMobile: true);
            iosSettings.maxTextureSize = _component._platformSettings.maxAtlasSizeIOS;
            importer.SetPlatformTextureSettings(iosSettings);
        }

        private TextureImporterFormat MapFormatToImporter(TextureCompressionFormat format, bool isMobile)
        {
            return format switch
            {
                TextureCompressionFormat.None => TextureImporterFormat.RGBA32,
                TextureCompressionFormat.BC7 => TextureImporterFormat.BC7,
                TextureCompressionFormat.BC1 => TextureImporterFormat.DXT1,
                TextureCompressionFormat.BC3 => TextureImporterFormat.DXT5,
                TextureCompressionFormat.BC4 => TextureImporterFormat.BC4,
                TextureCompressionFormat.BC5 => TextureImporterFormat.BC5,
                TextureCompressionFormat.ASTC => TextureImporterFormat.ASTC_6x6,
                TextureCompressionFormat.ETC2 => isMobile ? TextureImporterFormat.ETC2_RGBA8 : TextureImporterFormat.DXT5,
                TextureCompressionFormat.RGBA32 => TextureImporterFormat.RGBA32,
                _ => TextureImporterFormat.Automatic
            };
        }

        private void RemoveNPOTUnsupportedFormats(TextureImporter importer)
        {
            // PVRTC does not support NPOT - remove iOS PVRTC settings
            if (_component._platformSettings.overrideIOS)
            {
                var iosSettings = importer.GetPlatformTextureSettings("iPhone");
                if (iosSettings.format == TextureImporterFormat.PVRTC_RGB2 ||
                    iosSettings.format == TextureImporterFormat.PVRTC_RGB4 ||
                    iosSettings.format == TextureImporterFormat.PVRTC_RGBA2 ||
                    iosSettings.format == TextureImporterFormat.PVRTC_RGBA4)
                {
                    iosSettings.format = TextureImporterFormat.ASTC_6x6;
                    importer.SetPlatformTextureSettings(iosSettings);
                    _log.Warning("NPOT mode: replaced unsupported PVRTC format with ASTC for iOS. / NPOT 模式下将不支持的 PVRTC 替换为 ASTC。");
                }
            }
        }

        private TextureCategory DetermineCategory(GeneratedAtlas atlas, TextureTypeGroup tg)
        {
            if (tg.HasNormal) return TextureCategory.Normal;
            if (tg.HasMask) return TextureCategory.Mask;
            // Check if atlas has alpha
            if (atlas.Texture != null && GraphicsFormatUtility.HasAlphaChannel(atlas.Texture.graphicsFormat))
                return TextureCategory.Color;
            return TextureCategory.ColorOpaque;
        }
    }
}
