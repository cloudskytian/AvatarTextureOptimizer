// ATO — Avatar Texture Optimizer
// User-extensible i18n via JSON files under the package's Localization folder.
// 通过包内 Localization 文件夹下的 JSON 文件实现可扩展的 i18n。
//
// - Any number of languages: every *.json file in the Localization folder is one language
//   (file name without extension = language code, e.g. "en.json", "zh-Hans.json").
// - "Auto" follows NDMF's current language (LanguagePrefs.Language), with a
//   language-family fallback (e.g. zh-Hant → zh-Hans) and finally English.
// - Missing keys fall back to English, then to the key itself.
// 任意语言数量：Localization 文件夹里每个 *.json 就是一种语言（文件名去掉扩展名即语言码）。
// "Auto" 跟随 NDMF 当前语言（LanguagePrefs.Language），带语族回退（如 zh-Hant → zh-Hans），
// 最终回退英文。缺失的键回退到英文，再回退到键本身。

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Localized string keys. 本地化字符串键。
    /// </summary>
    public static class ATOI18nKeys
    {
        public const string Name = "ato.name";
        public const string Description = "ato.description";
        public const string Enable = "ato.enable";
        public const string GenerateAtlas = "ato.generateAtlas";
        public const string GenerateAtlasTooltip = "ato.generateAtlas.tooltip";
        public const string Quality = "ato.quality";
        public const string QualityPrefix = "ato.quality.";
        public const string Advanced = "ato.advanced";
        public const string AdvancedMsSsim = "ato.advanced.msSsim";
        public const string AdvancedDeltaE = "ato.advanced.deltaE";
        public const string AdvancedNormalAngle = "ato.advanced.normalAngle";
        public const string AdvancedNormalAngleP95 = "ato.advanced.normalAngleP95";
        public const string AdvancedAlphaRmse = "ato.advanced.alphaRmse";
        public const string AdvancedAlphaIou = "ato.advanced.alphaIou";
        public const string AdvancedGrayRmse = "ato.advanced.grayRmse";
        public const string Padding = "ato.padding";
        public const string PaddingTooltip = "ato.padding.tooltip";
        public const string DensityMin = "ato.density.min";
        public const string DensityMax = "ato.density.max";
        public const string DensityTooltip = "ato.density.tooltip";
        public const string Npot = "ato.npot";
        public const string NpotTooltip = "ato.npot.tooltip";
        public const string DedupMaterials = "ato.dedup.materials";
        public const string DedupTextures = "ato.dedup.textures";
        public const string Mipstreaming = "ato.mipstreaming";
        public const string MipstreamingTooltip = "ato.mipstreaming.tooltip";
        public const string Whitelist = "ato.whitelist";
        public const string WhitelistTooltip = "ato.whitelist.tooltip";
        public const string WhitelistEmpty = "ato.whitelist.empty";
        public const string Platform = "ato.platform";
        public const string PlatformAuto = "ato.platform.auto";
        public const string PlatformOverride = "ato.platform.override";
        public const string PlatformOverrideTooltip = "ato.platform.override.tooltip";
        public const string Compression = "ato.compression";
        public const string CompressionColor = "ato.compression.color";
        public const string CompressionColorTransparent = "ato.compression.colorTransparent";
        public const string CompressionNormal = "ato.compression.normal";
        public const string CompressionGrayscale = "ato.compression.grayscale";
        public const string CompressionDefault = "ato.compression.default";
        public const string CompressionPrefix = "ato.compression.";
        public const string CompressionGraySingleChannel = "ato.compression.graySingleChannel";
        public const string Language = "ato.language";
        public const string LanguageAuto = "ato.language.auto";
        public const string Verbosity = "ato.verbosity";
        public const string VerbosityTooltip = "ato.verbosity.tooltip";

        public const string ReportTitle = "ato.report.title";
        public const string ReportSummary = "ato.report.summary";
        public const string ReportDetails = "ato.report.details";
        public const string ReportTexturesProcessed = "ato.report.texturesProcessed";
        public const string ReportAtlasesGenerated = "ato.report.atlasesGenerated";
        public const string ReportAtlasSize = "ato.report.atlasSize";
        public const string ReportUtilization = "ato.report.utilization";
        public const string ReportIslands = "ato.report.islands";
        public const string ReportSources = "ato.report.sources";
        public const string ReportSavedTotal = "ato.report.savedTotal";
        public const string ReportElapsed = "ato.report.elapsed";
        public const string ReportStage = "ato.report.stage";
        public const string ReportTime = "ato.report.time";
        public const string ReportNothing = "ato.report.nothing";

        public const string ErrorMultipleComponents = "ato.error.multipleComponents";
        public const string ErrorNoDescriptor = "ato.error.noDescriptor";
        public const string ErrorNoTextureReadable = "ato.error.noTextureReadable";
        public const string ErrorInternal = "ato.error.internal";

        public const string WarnWhitelistSkip = "ato.warn.whitelistSkip";
        public const string WarnUnsupportedShader = "ato.warn.unsupportedShader";
        public const string WarnStTransform = "ato.warn.stTransform";
        public const string WarnDecal = "ato.warn.decal";
        public const string WarnUvOutOfBounds = "ato.warn.uvOutOfBounds";
        public const string WarnCannotFitAtlas = "ato.warn.cannotFitAtlas";
        public const string WarnGrayscaleMultiChannel = "ato.warn.grayscaleMultiChannel";
        public const string WarnAlphaFormatMissing = "ato.warn.alphaFormatMissing";
        public const string WarnNpotFormatExcluded = "ato.warn.npotFormatExcluded";
        public const string WarnAnimationTransform = "ato.warn.animationTransform";
        public const string WarnMeshScaleAnimated = "ato.warn.meshScaleAnimated";
        public const string WarnNoAAO = "ato.warn.noAAO";
        public const string WarnFallback = "ato.warn.fallback";

        public const string StageValidate = "ato.stage.validate";
        public const string StageAnalyze = "ato.stage.analyze";
        public const string StageOptimize = "ato.stage.optimize";
        public const string StageAtlas = "ato.stage.atlas";
        public const string StageReassign = "ato.stage.reassign";
        public const string StageDedup = "ato.stage.dedup";
        public const string StageReport = "ato.stage.report";
        public const string StageCleanup = "ato.stage.cleanup";
        public const string ProgressCancel = "ato.progress.cancel";
        public const string ProgressCancelled = "ato.progress.cancelled";
    }

    /// <summary>
    /// Localization service. 本地化服务。
    /// </summary>
    public static class ATOI18n
    {
        private static Dictionary<string, Dictionary<string, string>> _tables;
        private static string _currentCode = "en";
        private static Dictionary<string, string> _current;
        private static Dictionary<string, string> _english;

        private const string EnglishCode = "en";

        /// <summary>All available language codes (from files). 全部可用语言码（来自文件）。</summary>
        public static IReadOnlyList<string> AvailableLanguages
        {
            get { EnsureLoaded(); return _available; }
        }
        private static List<string> _available = new List<string>();

        /// <summary>The currently active language code. 当前生效语言码。</summary>
        public static string CurrentLanguage
        {
            get { EnsureLoaded(); return _currentCode; }
        }

        private static void EnsureLoaded()
        {
            if (_tables != null) return;
            _tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            _available = new List<string>();

            foreach (var guid in AssetDatabase.FindAssets("t:TextAsset"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.Contains("avatar-texture-optimizer", StringComparison.OrdinalIgnoreCase)) continue;
                if (!path.Contains("Localization", StringComparison.OrdinalIgnoreCase)) continue;
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (asset == null) continue;
                string code = Path.GetFileNameWithoutExtension(path);
                var table = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    object root = ATOMiniJson.Parse(asset.text);
                    if (root is Dictionary<string, object> dict)
                    {
                        foreach (var kv in dict)
                        {
                            if (kv.Value is string s) table[kv.Key] = s;
                            else if (kv.Value != null) table[kv.Key] = kv.Value.ToString();
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ATO] Failed to parse i18n file '{path}': {e.Message}");
                }

                _tables[code] = table;
                _available.Add(code);
            }

            if (!_tables.TryGetValue(EnglishCode, out _english))
                _english = new Dictionary<string, string>(StringComparer.Ordinal);

            _current = _english;
            _currentCode = EnglishCode;
        }

        /// <summary>
        /// Set the active language. "auto" resolves via NDMF's language preference.
        /// 设置当前语言。"auto" 通过 NDMF 语言偏好解析。
        /// </summary>
        public static void SetLanguage(string requested)
        {
            EnsureLoaded();
            _currentCode = Resolve(requested);
            _current = _tables.TryGetValue(_currentCode, out var t) ? t : _english;
        }

        private static string Resolve(string requested)
        {
            if (string.IsNullOrEmpty(requested) || requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                string ndmf = ReadNdmfLanguage();
                return MatchTable(ndmf);
            }
            return MatchTable(requested);
        }

        private static string ReadNdmfLanguage()
        {
            try
            {
                // NDMF is a hard dependency; LanguagePrefs lives in nadena.dev.ndmf.localization.
                // NDMF 是硬依赖；LanguagePrefs 位于 nadena.dev.ndmf.localization。
                return nadena.dev.ndmf.localization.LanguagePrefs.Language;
            }
            catch (Exception)
            {
                return EnglishCode;
            }
        }

        private static string MatchTable(string code)
        {
            if (string.IsNullOrEmpty(code)) return EnglishCode;
            code = code.ToLowerInvariant().Trim();
            // Exact match. 精确匹配。
            if (_tables.ContainsKey(code)) return code;

            // Language-family match (e.g. zh-hant → zh-hans, en-us → en).
            // 语族匹配（如 zh-hant → zh-hans、en-us → en）。
            string family = code.Split('-')[0];
            string best = null;
            foreach (var lang in _tables.Keys)
            {
                if (lang.ToLowerInvariant().StartsWith(family, StringComparison.Ordinal))
                {
                    best = lang;
                    break;
                }
            }
            if (best != null) return best;
            return EnglishCode;
        }

        /// <summary>
        /// Translate a key with optional format arguments.
        /// Fallback order: active language → English → the key itself.
        /// 翻译键，可带格式化参数。回退顺序：当前语言 → 英文 → 键本身。
        /// </summary>
        public static string T(string key, params object[] args)
        {
            EnsureLoaded();
            string text = null;
            if (_current != null) _current.TryGetValue(key, out text);
            if (text == null && _english != null && !ReferenceEquals(_current, _english))
                _english.TryGetValue(key, out text);
            if (text == null) text = key;

            if (args != null && args.Length > 0)
            {
                try { text = string.Format(text, args); }
                catch (FormatException) { /* keep raw text 保留原文 */ }
            }
            return text;
        }
    }
}
