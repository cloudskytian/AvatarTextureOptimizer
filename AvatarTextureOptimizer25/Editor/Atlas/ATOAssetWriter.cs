// Avatar Texture Optimizer / 头像贴图优化器
// Writes generated PNGs to disk, configures TextureImporter (per-platform
// overrides, mipmaps+streaming binding, clamp-only atlases, sRGB/normal
// handling, safe-format fallbacks), and caches results by content+settings
// hash so unchanged outputs skip re-import on later builds.
// 将生成 PNG 写入磁盘并配置 TextureImporter（逐平台覆盖、Mipmap+Streaming 绑定、
// 图集强制 Clamp、sRGB/法线处理、安全格式兜底），并用 内容+导入参数 哈希缓存，
// 未变化的产物在后续构建中免重复导入。
//
// Generated assets live in Assets/AvatarTextureOptimizer-Generated. They are
// cleaned at the start of the next build (stale builds only) or via the
// Tools menu; cancelled builds keep them on disk per requirement.
// 生成资产位于 Assets/AvatarTextureOptimizer-Generated。下次构建开始时清理
// 旧产物，或通过 Tools 菜单手动清理；取消的构建会将其保留在磁盘上。

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>A written & imported texture asset. / 一份已写入并导入的贴图资产。</summary>
    public sealed class ATOWrittenTexture
    {
        public string assetPath;
        public Texture2D texture;
        public ATOTextureCategory category;
        public long bytes;
        public int width, height;
        public string name;
        public float layerScale = 1f;
    }

    /// <summary>Category classification of generated content. / 生成内容的分类判定。</summary>
    public static class ATOCategoryClassifier
    {
        /// <summary>Classify an atlas layer into a texture category. / 将图集层分类为贴图类别。</summary>
        public static ATOTextureCategory ForLayer(ATORole role, bool hasAlpha)
        {
            switch (role)
            {
                case ATORole.Normal: return ATOTextureCategory.Normal;
                case ATORole.Mask: return ATOTextureCategory.Grayscale;
                default: return hasAlpha ? ATOTextureCategory.Transparent : ATOTextureCategory.Opaque;
            }
        }

        /// <summary>Re-classify "grayscale" content down to single-channel-safe eligibility. / 校验灰度内容是否真的可以单通道存储。</summary>
        public static bool IsTrulySingleChannel(Color32[] pixels, int channel)
        {
            // Single channel semantics require other channels to be (near-)constant
            // duplicates of the used channel OR unused in every sampled pixel.
            // 单通道语义要求其余通道与使用通道（近似）一致或完全未被使用。
            switch (channel)
            {
                case 0:
                    foreach (var p in pixels)
                        if (Mathf.Abs(p.g - p.r) > 2 || Mathf.Abs(p.b - p.r) > 2) return false;
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Maps ATO's safe enums to TextureImporterFormat with platform/category
    /// constraints and fallbacks.
    /// 将 ATO 安全枚举映射为 TextureImporterFormat，带平台/类别约束与兜底。
    /// </summary>
    public static class ATOFormatMapping
    {
        public static ATOEncodingFormat ResolveBest(ATOTextureCategory cat, ATOPlatform platform)
        {
            switch (cat)
            {
                case ATOTextureCategory.Normal:
                    return platform == ATOPlatform.PC ? ATOEncodingFormat.BC7 : ATOEncodingFormat.ASTC_6x6;
                case ATOTextureCategory.Grayscale:
                    return platform == ATOPlatform.PC ? ATOEncodingFormat.BC7 : ATOEncodingFormat.ASTC_6x6;
                case ATOTextureCategory.Transparent:
                    return platform == ATOPlatform.PC ? ATOEncodingFormat.BC7 : ATOEncodingFormat.ASTC_6x6;
                default:
                    return platform == ATOPlatform.PC ? ATOEncodingFormat.BC7 : ATOEncodingFormat.ASTC_6x6;
            }
        }

        /// <summary>Validate a user-picked format for category/platform; fallback with reasons. / 校验用户格式并带原因兜底。</summary>
        public static ATOEncodingFormat Sanitize(
            ATOEncodingFormat chosen, ATOTextureCategory cat, ATOPlatform platform,
            bool npot, out string sanitizeNote)
        {
            sanitizeNote = null;
            if (chosen == ATOEncodingFormat.Auto) return ResolveBest(cat, platform);

            bool needsAlpha = cat == ATOTextureCategory.Transparent;
            bool isPvrtc = chosen == ATOEncodingFormat.PVRTC_RGB4 || chosen == ATOEncodingFormat.PVRTC_RGBA4;

            if (needsAlpha && !FormatHasAlpha(chosen))
            {
                sanitizeNote = ATOLoc.T("ato:format.alpha_fallback", chosen, cat);
                return ResolveBest(cat, platform);
            }
            // Per-category safe format enumeration (spec): reject formats that
            // would silently corrupt this category's content.
            // 按类别的格式安全枚举（需求）：拒绝会静默毁坏本类内容的格式。
            if (!IsCompatible(chosen, cat))
            {
                sanitizeNote = ATOLoc.T("ato:format.category_incompatible", chosen, cat);
                return ResolveBest(cat, platform);
            }
            if (npot && isPvrtc && platform == ATOPlatform.iOS)
            {
                sanitizeNote = ATOLoc.T("ato:format.pvrtc_npot", chosen);
                return ResolveBest(cat, platform);
            }
            if (platform != ATOPlatform.PC &&
                (chosen == ATOEncodingFormat.DXT1 || chosen == ATOEncodingFormat.DXT5 ||
                 chosen == ATOEncodingFormat.BC5 || chosen == ATOEncodingFormat.BC7))
            {
                sanitizeNote = ATOLoc.T("ato:format.pc_only", chosen, platform);
                return ResolveBest(cat, platform);
            }
            if (platform == ATOPlatform.PC && IsMobileOnly(chosen))
            {
                sanitizeNote = ATOLoc.T("ato:format.mobile_only", chosen, platform);
                return ResolveBest(cat, platform);
            }
            return chosen;
        }

        /// <summary>
        /// Category-safe membership: excludes formats that would drop channels
        /// the category needs (e.g. R8/R16 for normals or color, block color
        /// formats for single-channel grayscale intent).
        /// 类别安全成员检查：排除会丢失本类别所需通道的格式（如法线/彩色用
        /// R8/R16，或灰度内容用块状彩色格式以外的特例）。
        /// </summary>
        private static bool IsCompatible(ATOEncodingFormat f, ATOTextureCategory cat)
        {
            switch (cat)
            {
                case ATOTextureCategory.Normal:
                    // Only formats preserving 3-4 channels are acceptable for normals.
                    switch (f)
                    {
                        case ATOEncodingFormat.RGBA32:
                        case ATOEncodingFormat.ARGB32:
                        case ATOEncodingFormat.BC5:
                        case ATOEncodingFormat.BC7:
                        case ATOEncodingFormat.ASTC_6x6:
                        case ATOEncodingFormat.ASTC_4x4:
                        case ATOEncodingFormat.ASTC_8x8:
                        case ATOEncodingFormat.ETC2_RGBA8:
                            return true;
                        default:
                            return false;
                    }
                case ATOTextureCategory.Grayscale:
                    // Single-channel or full-fidelity formats only.
                    switch (f)
                    {
                        case ATOEncodingFormat.R8:
                        case ATOEncodingFormat.R16:
                        case ATOEncodingFormat.RGBA32:
                        case ATOEncodingFormat.ARGB32:
                        case ATOEncodingFormat.BC7:
                        case ATOEncodingFormat.ASTC_6x6:
                        case ATOEncodingFormat.ASTC_4x4:
                        case ATOEncodingFormat.ASTC_8x8:
                        case ATOEncodingFormat.ETC2_RGBA8:
                        case ATOEncodingFormat.DXT1: // grayscale survives DXT1 (R≈G≈B)
                            return true;
                        default:
                            return false;
                    }
                case ATOTextureCategory.Transparent:
                    // Alpha requirement handled by FormatHasAlpha above; additionally
                    // exclude single-channel formats (they cannot carry color+alpha).
                    switch (f)
                    {
                        case ATOEncodingFormat.R8:
                        case ATOEncodingFormat.R16:
                            return false;
                        default:
                            return FormatHasAlpha(f);
                    }
                default: // Opaque: exclude single-channel (would drop g/b content)
                    switch (f)
                    {
                        case ATOEncodingFormat.R8:
                        case ATOEncodingFormat.R16:
                            return false;
                        default:
                            return true;
                    }
            }
        }

        private static bool IsMobileOnly(ATOEncodingFormat f)
        {
            switch (f)
            {
                case ATOEncodingFormat.PVRTC_RGB4:
                case ATOEncodingFormat.PVRTC_RGBA4:
                case ATOEncodingFormat.ETC2_RGB4:
                case ATOEncodingFormat.ETC2_RGBA8:
                case ATOEncodingFormat.ASTC_4x4:
                case ATOEncodingFormat.ASTC_6x6:
                case ATOEncodingFormat.ASTC_8x8:
                    return true;
                default:
                    return false;
            }
        }

        public static bool FormatHasAlpha(ATOEncodingFormat f)
        {
            switch (f)
            {
                case ATOEncodingFormat.RGB24:
                case ATOEncodingFormat.DXT1:
                case ATOEncodingFormat.ETC2_RGB4:
                case ATOEncodingFormat.PVRTC_RGB4:
                    return false;
                default:
                    return true;
            }
        }

        public static TextureImporterFormat ToImporter(ATOEncodingFormat f)
        {
            switch (f)
            {
                case ATOEncodingFormat.RGBA32: return TextureImporterFormat.RGBA32;
                case ATOEncodingFormat.ARGB32: return TextureImporterFormat.ARGB32;
                case ATOEncodingFormat.RGB24: return TextureImporterFormat.RGB24;
                case ATOEncodingFormat.DXT1: return TextureImporterFormat.DXT1;
                case ATOEncodingFormat.DXT5: return TextureImporterFormat.DXT5;
                case ATOEncodingFormat.BC5: return TextureImporterFormat.BC5;
                case ATOEncodingFormat.BC7: return TextureImporterFormat.BC7;
                case ATOEncodingFormat.ASTC_4x4: return TextureImporterFormat.ASTC_RGBA_4x4;
                case ATOEncodingFormat.ASTC_6x6: return TextureImporterFormat.ASTC_RGBA_6x6;
                case ATOEncodingFormat.ASTC_8x8: return TextureImporterFormat.ASTC_RGBA_8x8;
                case ATOEncodingFormat.ETC2_RGB4: return TextureImporterFormat.ETC2_RGB4;
                case ATOEncodingFormat.ETC2_RGBA8: return TextureImporterFormat.ETC2_RGBA8;
                case ATOEncodingFormat.PVRTC_RGB4: return TextureImporterFormat.PVRTC_RGB4;
                case ATOEncodingFormat.PVRTC_RGBA4: return TextureImporterFormat.PVRTC_RGBA4;
                case ATOEncodingFormat.R8: return TextureImporterFormat.R8;
                case ATOEncodingFormat.R16: return TextureImporterFormat.R16;
                default: return TextureImporterFormat.Automatic;
            }
        }
    }

    /// <summary>
    /// Writes generated textures to disk with full importer setup and caching.
    /// 将生成贴图带完整导入设置写盘，并做缓存。
    /// </summary>
    public sealed class ATOAssetWriter
    {
        private readonly AvatarTextureOptimizer _settings;
        private readonly ATOBuildReport _report;
        private readonly string _buildFolder;
        private bool _startedAssetEditing;
        // Session-level content dedup: identical content+class generated textures
        // (e.g. two atlases with identical pixels & settings) share one asset.
        // 会话级内容去重：内容与分类完全一致的生成贴图共享一份资产。
        private readonly Dictionary<string, ATOWrittenTexture> _contentDedup = new Dictionary<string, ATOWrittenTexture>();

        private readonly ATOPlatform _buildPlatform;

        public ATOAssetWriter(AvatarTextureOptimizer settings, ATOBuildReport report, string buildId,
            ATOPlatform buildPlatform = ATOPlatform.PC)
        {
            _settings = settings;
            _report = report;
            _buildFolder = ATOConsts.GeneratedRoot + "/" + Sanitize(buildId);
            _buildPlatform = buildPlatform;
        }

        private static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        /// <summary>Begin a batched import session. / 开始批量导入会话。</summary>
        public void BeginBatch()
        {
            if (_startedAssetEditing) return;
            _startedAssetEditing = true;
            try
            {
                AssetDatabase.StartAssetEditing();
            }
            catch
            {
                _startedAssetEditing = false;
            }
            EnsureFolder(ATOConsts.GeneratedRoot);
            EnsureFolder(_buildFolder);
        }

        /// <summary>End the batch, importing everything once. / 结束批量，统一导入。</summary>
        public void EndBatch()
        {
            if (!_startedAssetEditing) return;
            _startedAssetEditing = false;
            try
            {
                AssetDatabase.StopAssetEditing();
            }
            catch (Exception e)
            {
                ATOLog.Warn("StopAssetEditing failed: " + e.Message);
            }
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            var leaf = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent ?? "Assets", leaf);
        }

        /// <summary>
        /// Write one PNG texture; reuse the cached import when content+settings
        /// match (stored in importer userData).
        /// 写入一张 PNG；当 内容+设置 匹配时复用既有导入（存储于 importer userData）。
        /// </summary>
        public ATOWrittenTexture Write(
            string name, ATOGeneratedLayer layer, ATOTextureCategory category,
            FilterMode filter, out string cacheNote,
            TextureWrapMode wrapU = TextureWrapMode.Clamp, TextureWrapMode wrapV = TextureWrapMode.Clamp)
        {
            BeginBatch();
            // Safe fallback (spec): content classified Grayscale but actually
            // multi-channel must not go to single-channel formats.
            // 安全兜底（需求）：分类为灰度但内容实为多通道时禁止去单通道格式。
            if (category == ATOTextureCategory.Grayscale && !layer.isEffectivelyGray)
            {
                category = layer.hasAlpha ? ATOTextureCategory.Transparent : ATOTextureCategory.Opaque;
                var warn = ATOLoc.T("ato:format.gray_multichannel_fallback", name);
                _report.warnings.Add(warn);
                ATOLog.Warn(warn);
            }
            var hash = Hash(layer.pngBytes, layer, category, filter, wrapU, wrapV);
            // Session content dedup by hash / 会话内容按哈希去重
            if (_contentDedup.TryGetValue(hash, out var existing) &&
                existing != null && existing.category == category)
            {
                cacheNote = "content-dedup";
                return existing;
            }
            var path = $"{_buildFolder}/{name}.png";
            if (TryReuseCached(path, hash, out var cachedTex))
            {
                cacheNote = "cache-hit";
                cachedTex.category = category;
                _contentDedup[hash] = cachedTex;
                return cachedTex;
            }

            File.WriteAllText(path + ".atojson", BuildMetaJson(hash));
            File.WriteAllBytes(path, layer.pngBytes);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            ConfigureImporter(importer, layer, category, filter, wrapU, wrapV);
            importer.userData = ATOConsts.CacheUserDataPrefix + hash;
            importer.SaveAndReimport();

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            cacheNote = "written";
            var written = new ATOWrittenTexture
            {
                assetPath = path,
                texture = tex,
                category = category,
                bytes = layer.pngBytes.LongLength,
                width = layer.width,
                height = layer.height,
                name = name,
            };
            _contentDedup[hash] = written;
            return written;
        }

        private bool TryReuseCached(string path, string hash, out ATOWrittenTexture result)
        {
            result = null;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return false;
            var expected = ATOConsts.CacheUserDataPrefix + hash;
            if (importer.userData != expected) return false;
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null) return false;
            result = new ATOWrittenTexture
            {
                assetPath = path,
                texture = tex,
                bytes = new FileInfo(path).Length,
                width = tex.width,
                height = tex.height,
                name = Path.GetFileNameWithoutExtension(path),
            };
            return true;
        }

        private string Hash(byte[] png, ATOGeneratedLayer layer, ATOTextureCategory cat,
            FilterMode filter, TextureWrapMode wrapU, TextureWrapMode wrapV)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(png);
                var sb = new StringBuilder(160);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                sb.Append('|').Append(layer.sRGB ? 1 : 0).Append('|').Append((int)cat)
                    .Append('|').Append(layer.width).Append('x').Append(layer.height)
                    .Append('|').Append(_settings.qualityPreset)
                    .Append('|').Append((int)filter)
                    .Append("|wu").Append((int)wrapU).Append("|wv").Append((int)wrapV)
                    .Append("|npot").Append(_settings.experimentalNPOT ? 1 : 0)
                    // Every platform's effective rule for THIS category invalidates
                    // the cache (QA-1: format changes with enabled override reused
                    // stale imports). / 本类别下全部平台的有效规则都参与缓存失效。
                    .Append('|').Append(RuleFor(cat, ATOPlatform.PC).HashKey())
                    .Append('|').Append(RuleFor(cat, ATOPlatform.Android).HashKey())
                    .Append('|').Append(RuleFor(cat, ATOPlatform.iOS).HashKey());
                return sb.ToString();
            }
        }

        private string BuildMetaJson(string hash)
        {
            var d = new Dictionary<string, object>
            {
                ["tool"] = "AvatarTextureOptimizer",
                ["version"] = "0.1.0",
                ["hash"] = hash,
            };
            return ATOJson.Write(d);
        }

        private void ConfigureImporter(TextureImporter imp, ATOGeneratedLayer layer, ATOTextureCategory category,
            FilterMode filter, TextureWrapMode wrapU, TextureWrapMode wrapV)
        {
            imp.textureType = category == ATOTextureCategory.Normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            imp.sRGBTexture = layer.sRGB && category != ATOTextureCategory.Normal && category != ATOTextureCategory.Grayscale;
            imp.alphaIsTransparency = category == ATOTextureCategory.Transparent;
            // Mipmap+streaming rule of the CURRENT build platform (mipmaps are an
            // importer-global setting, not per-platform). / 取当前构建平台的
            // mipmap+流送规则（mipmap 是导入器全局设置，不支持逐平台）。
            var mipRule = RuleFor(category, _buildPlatform);
            imp.mipmapEnabled = mipRule.mipmapsAndStreaming;
            imp.streamingMipmaps = mipRule.mipmapsAndStreaming; // bound / 绑定
            imp.filterMode = filter;
            // Atlases are clamp-only; standalone scaled textures keep the source
            // wrap (tiled UVs would break otherwise, QA-1). / 图集强制 Clamp；
            // 整图缩放贴图必须保留源 wrap（否则平铺 UV 被破坏）。
            imp.wrapU = wrapU;
            imp.wrapV = wrapV;
            imp.isReadable = false; // never Read/Write / 永不 Read/Write
            imp.npotScale = _settings.experimentalNPOT ? TextureImporterNPOTScale.None : TextureImporterNPOTScale.ToNearest;
            imp.maxTextureSize = Mathf.Max(layer.width, layer.height);

            ConfigurePlatform(imp, "Standalone", ATOPlatform.PC, category, layer);
            ConfigurePlatform(imp, "Android", ATOPlatform.Android, category, layer);
            ConfigurePlatform(imp, "iPhone", ATOPlatform.iOS, category, layer);
        }

        private ATOCategoryRule RuleFor(ATOTextureCategory cat, ATOPlatform platform)
        {
            var ov = _settings.OverrideFor(platform);
            if (ov != null && ov.enabled) return ov.RuleFor(cat);
            return DefaultRules.Default(cat);
        }

        private void ConfigurePlatform(
            TextureImporter imp, string platformName, ATOPlatform platform,
            ATOTextureCategory category, ATOGeneratedLayer layer)
        {
            var rule = RuleFor(category, platform);
            var fmt = ATOFormatMapping.Sanitize(rule.format, category, platform,
                _settings.experimentalNPOT, out var note);
            if (note != null)
            {
                _report.warnings.Add(note);
                ATOLog.Warn(note);
            }
            var settings = new TextureImporterPlatformSettings
            {
                name = platformName,
                overridden = true,
                maxTextureSize = Mathf.Max(layer.width, layer.height),
                format = ATOFormatMapping.ToImporter(fmt),
                textureCompression = fmt == ATOEncodingFormat.Auto
                    ? TextureImporterCompression.Compressed
                    : (fmt == ATOEncodingFormat.RGBA32 || fmt == ATOEncodingFormat.ARGB32 || fmt == ATOEncodingFormat.RGB24
                        ? TextureImporterCompression.Uncompressed
                        : TextureImporterCompression.Compressed),
                compressionQuality = rule.compressorQuality,
                crunchedCompression = rule.crunch && SupportsCrunch(fmt),
            };
            imp.SetPlatformTextureSettings(settings);
        }

        private static bool SupportsCrunch(ATOEncodingFormat f) =>
            f == ATOEncodingFormat.DXT1 || f == ATOEncodingFormat.DXT5 ||
            f == ATOEncodingFormat.ETC2_RGB4 || f == ATOEncodingFormat.ETC2_RGBA8;

        /// <summary>Clean stale generated folders from previous builds/cancels. / 清理先前构建/取消遗留的生成目录。</summary>
        public static void CleanStaleGenerated(string currentBuildId)
        {
            if (!AssetDatabase.IsValidFolder(ATOConsts.GeneratedRoot)) return;
            foreach (var sub in AssetDatabase.GetSubFolders(ATOConsts.GeneratedRoot))
            {
                if (sub.EndsWith("/" + currentBuildId, StringComparison.Ordinal)) continue;
                try
                {
                    AssetDatabase.DeleteAsset(sub);
                    ATOLog.Verbose("cleaned stale folder: " + sub);
                }
                catch (Exception e)
                {
                    ATOLog.Warn("stale-clean failed for " + sub + ": " + e.Message);
                }
            }
        }

        /// <summary>Menu: manual cleanup of all generated folders. / 菜单：手动清理全部生成目录。</summary>
        [MenuItem("Tools/Avatar Texture Optimizer/Clean Generated Assets", false, 101)]
        private static void MenuClean()
        {
            if (AssetDatabase.IsValidFolder(ATOConsts.GeneratedRoot))
            {
                AssetDatabase.DeleteAsset(ATOConsts.GeneratedRoot);
                AssetDatabase.Refresh();
                ATOLog.Info("generated assets cleaned / 生成资产已清理");
            }
        }
    }

    /// <summary>Built-in default rules when a platform override is disabled. / 平台覆盖未启用时的内置默认规则。</summary>
    public static class DefaultRules
    {
        public static ATOCategoryRule Default(ATOTextureCategory cat)
        {
            return new ATOCategoryRule
            {
                format = ATOEncodingFormat.Auto,
                crunch = false,
                compressorQuality = 50,
                mipmapsAndStreaming = true,
            };
        }

        /// <summary>Generate a stable short build id for the current session. / 生成当前会话的稳定短构建 ID。</summary>
        public static string NewBuildId()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }
    }
}
