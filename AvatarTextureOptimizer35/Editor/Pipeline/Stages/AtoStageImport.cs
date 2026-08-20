using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Stage: apply texture import parameters. / 阶段：应用贴图导入参数。
    /// Atlases: Read/Write off + Clamp forced (not user-configurable), mipmaps+streaming bound to
    /// one switch, compression per category × platform override (safe enumeration with fallbacks),
    /// other parameters take the highest quality among the source textures. Fallback (non-atlas,
    /// non-whitelisted) textures get the same treatment except wrap mode. Whitelisted textures are
    /// untouched. / 图集：Read/Write 关 + 强制 Clamp（不可修改），Mipmap+Streaming 单开关绑定，
    /// 压缩按分类 × 平台 override（安全枚举带回退），其余参数取来源贴图中质量最高者。fallback
    /// （非图集、非白名单）贴图除 wrap 外同样处理。白名单贴图不动。
    /// </summary>
    internal sealed class AtoStageImport : IAtoStage
    {
        public string I18nKey => "import";

        public void Run(AtoContext ctx)
        {
            var settings = ctx.State.Settings;

            // ---- atlases ----
            foreach (var group in ctx.TypeGroups)
            {
                foreach (var atlas in group.Atlases)
                {
                    ctx.State.ThrowIfCancelled();
                    if (atlas.Result == null) continue;
                    var importer = GetImporter(atlas.Result);
                    if (importer == null) continue;

                    var category = CategoryFor(group, atlas);
                    ApplySettings(ctx, importer, atlas.Result, category,
                        clamp: true, forceReadableOff: true,
                        keepWrap: false, atlas: true, srgbOverride: group.Key.Srgb);
                    AtoLog.Verbose($"[ATO] import settings applied to atlas {atlas.Name} ({category})");
                }
            }

            // ---- fallback textures (non-atlas, non-whitelisted) ----
            foreach (var record in ctx.Textures.Values)
            {
                ctx.State.ThrowIfCancelled();
                if (record.Whitelisted) continue;
                if (record.InAtlas) continue;
                if (record.Result == null) continue;

                var importer = GetImporter(record.Result);
                if (importer == null) continue;

                var category = CategoryFor(record);
                var isGenerated = record.Result != record.Texture; // resized → generated. / 缩放过 → 生成资产。
                var srgb = AtoTextureIO.GetImportSettings(record.Texture).SrgbTexture;
                ApplySettings(ctx, importer, record.Result, category,
                    clamp: isGenerated,
                    forceReadableOff: isGenerated,
                    keepWrap: !isGenerated,
                    atlas: false, srgbOverride: srgb);
                AtoLog.Verbose($"[ATO] import settings applied to {record.Texture.name}");
            }
        }

        private static TextureImporter GetImporter(Texture2D texture)
        {
            var path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetImporter.GetAtPath(path) as TextureImporter;
        }

        /// <summary>Category of an atlas (from its type group). / 图集分类（来自其类型组）。</summary>
        private static AtoTextureCategory CategoryFor(AtoTypeGroup group, AtoAtlas atlas)
        {
            var signature = group.Key.KindSignature;
            if (signature.Contains("Normal")) return AtoTextureCategory.NormalMap;
            if (signature.Contains("Mask") || signature.Contains("Tangent"))
                return AtoTextureCategory.Grayscale;
            return group.HasAlpha ? AtoTextureCategory.Transparent : AtoTextureCategory.Opaque;
        }

        /// <summary>Category of a texture record (from its usages). / 贴图记录分类（来自其用法）。</summary>
        private static AtoTextureCategory CategoryFor(AtoTextureRecord record)
        {
            var normal = record.Slots.Any(s => s.Usage.Kind == AtoTextureKind.Normal);
            if (normal) return AtoTextureCategory.NormalMap;
            var gray = record.Slots.All(s => s.Usage.Kind == AtoTextureKind.Mask || s.Usage.Kind == AtoTextureKind.Tangent);
            if (gray) return AtoTextureCategory.Grayscale;
            var transparent = record.Slots.Any(s => s.Usage.HasBlend || s.Usage.CutoutThresholds.Count > 0);
            return transparent ? AtoTextureCategory.Transparent : AtoTextureCategory.Opaque;
        }

        private void ApplySettings(AtoContext ctx, TextureImporter importer, Texture2D texture,
            AtoTextureCategory category, bool clamp, bool forceReadableOff, bool keepWrap, bool atlas,
            bool srgbOverride)
        {
            var settings = ctx.State.Settings;

            if (!keepWrap)
            {
                // Atlases: Clamp forced, not user-configurable. / 图集：强制 Clamp，不可修改。
                importer.wrapMode = TextureWrapMode.Clamp;
            }
            if (forceReadableOff)
            {
                // Atlases: Read/Write off, not user-configurable. / 图集：Read/Write 关，不可修改。
                importer.isReadable = false;
            }

            // Mipmaps + streaming: one switch controls both (VRChat requirement). /
            // Mipmap+Streaming：单开关同控（VRChat 要求）。
            importer.mipmapEnabled = settings.mipmapsAndStreaming;
            importer.streamingMipmaps = settings.mipmapsAndStreaming;

            // sRGB + normal type. / sRGB 与法线类型。
            if (category == AtoTextureCategory.NormalMap)
            {
                importer.textureType = TextureImporterType.NormalMap;
                importer.sRGBTexture = false;
            }
            else
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = srgbOverride;
            }

            importer.alphaIsTransparency = category is AtoTextureCategory.Transparent or AtoTextureCategory.Opaque;
            importer.maxTextureSize = Mathf.Clamp(Mathf.Max(texture.width, texture.height), 32, 8192);

            // Compression per category × platform override. / 压缩按分类 × 平台 override。
            var npot = atlas && settings.experimentalNpot;
            var general = settings.compression;
            foreach (var platform in new[] { AtoTargetPlatform.PC, AtoTargetPlatform.Android, AtoTargetPlatform.IOS })
            {
                var overrideEnabled = AtoPlatformUtil.IsOverrideEnabled(settings, platform);
                var config = overrideEnabled ? AtoPlatformUtil.GetOverride(settings, platform).compression : general;
                var requested = Requested(config, category);
                var format = AtoCompressionMapping.Resolve(ctx, requested, category, platform, npot, out var warning);
                if (!string.IsNullOrEmpty(warning)) ctx.Warn($"[ATO] {texture.name}: {warning}");

                var platformName = AtoPlatformUtil.ImporterPlatform(platform);
                importer.SetPlatformTextureSettings(platformName,
                    importer.maxTextureSize, format, 100, allowsAlphaSplitting: false);
            }

            // Single-channel grayscale only when the content really is single-channel. /
            // 灰度单通道格式仅在内容真为单通道时使用。
            var requestedGray = Requested(general, AtoTextureCategory.Grayscale);
            if (category == AtoTextureCategory.Grayscale &&
                (requestedGray == AtoCompressionFormat.R8 || requestedGray == AtoCompressionFormat.BC4) &&
                !AtoCompressionMapping.IsSingleChannelContent(ctx, texture))
            {
                ctx.Warn(ctx.State.Tr("warn.grayscaleFallback", texture.name, "RGBA-compatible format"));
                foreach (var platform in new[] { AtoTargetPlatform.PC, AtoTargetPlatform.Android, AtoTargetPlatform.IOS })
                {
                    var platformName = AtoPlatformUtil.ImporterPlatform(platform);
                    importer.SetPlatformTextureSettings(platformName,
                        importer.maxTextureSize,
                        AtoCompressionMapping.DefaultFor(AtoTextureCategory.Grayscale, platform),
                        100, allowsAlphaSplitting: false);
                }
            }

            importer.SaveAndReimport();
        }

        private static AtoCompressionFormat Requested(AtoCompressionConfig config, AtoTextureCategory category) =>
            category switch
            {
                AtoTextureCategory.Opaque => config.opaque,
                AtoTextureCategory.Transparent => config.transparent,
                AtoTextureCategory.NormalMap => config.normalMap,
                _ => config.grayscale,
            };
    }
}
