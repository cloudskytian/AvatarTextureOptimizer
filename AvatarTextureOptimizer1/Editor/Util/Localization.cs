// Localization.cs / Localization.cs
// i18n support for ATO. Loads JSON translation files from Resources/Localization.
// ATO的国际化支持。从Editor/Resources/Localization加载JSON翻译文件。
// JSON format: {"key": "value", ...} (simple flat object).
// JSON格式：{"key": "value", ...}（简单扁平对象）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Util
{
    /// <summary>
    /// Simple localisation system. Keys are dot-separated; values are loaded from JSON files.
    /// 简易本地化系统。键用点分隔；从JSON文件加载。
    /// </summary>
    public static class ATOLocalization
    {
        private static readonly Dictionary<string, Dictionary<string, string>> _tables = new();
        private static string _currentLang = "en";
        private static bool _initialized = false;

        private const string FallbackLang = "en";

        public static string[] AvailableLanguages
        {
            get { EnsureInitialized(); return new List<string>(_tables.Keys).ToArray(); }
        }

        public static string CurrentLanguage
        {
            get { EnsureInitialized(); return _currentLang; }
            set { _currentLang = value; }
        }

        public static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;
            LoadAllLanguages();

            // Try to match NDMF language preference / 尝试匹配NDMF语言偏好
            try
            {
                var ndmfLangPref = EditorPrefs.GetString("nadena.dev.ndmf.language", "");
                if (!string.IsNullOrEmpty(ndmfLangPref) && _tables.ContainsKey(ndmfLangPref))
                {
                    _currentLang = ndmfLangPref;
                    return;
                }
            }
            catch { /* ignore / 忽略 */ }

            // Fall back to system language / 回退到系统语言
            if (Application.systemLanguage == SystemLanguage.ChineseSimplified && _tables.ContainsKey("zh-CN"))
                _currentLang = "zh-CN";
            else if (_tables.ContainsKey(FallbackLang))
                _currentLang = FallbackLang;
        }

        private static void LoadAllLanguages()
        {
            try
            {
                var locFolder = GetLocalizationFolder();
                if (Directory.Exists(locFolder))
                {
                    foreach (var file in Directory.GetFiles(locFolder, "*.json"))
                    {
                        var lang = Path.GetFileNameWithoutExtension(file);
                        var json = File.ReadAllText(file);
                        var dict = ParseFlatJsonObject(json);
                        _tables[lang] = dict;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ATO] Failed to load localisation tables: {e.Message} / 加载本地化表失败：{e.Message}");
            }

            if (_tables.Count == 0)
            {
                _tables[FallbackLang] = new Dictionary<string, string>();
            }
        }

        /// <summary>
        /// Returns the localization folder absolute path.
        /// 返回本地化文件夹绝对路径。
        /// </summary>
        private static string GetLocalizationFolder()
        {
            // Find package by package.json name / 通过package.json名查找包
            var guids = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset net.fosa.avatar-texture-optimizer.Editor");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("net.fosa.avatar-texture-optimizer.Editor.asmdef"))
                {
                    var editorDir = Path.GetDirectoryName(path);
                    var pkgDir = Path.GetDirectoryName(editorDir);
                    var abs = Path.GetFullPath(pkgDir).Replace("\\", "/");
                    var locDir = abs + "/Editor/Resources/Localization";
                    return locDir;
                }
            }
            return Path.GetFullPath("Packages/net.fosa.avatar-texture-optimizer/Editor/Resources/Localization").Replace("\\", "/");
        }

        /// <summary>
        /// Parse a flat JSON object of the form {"key1":"value1","key2":"value2",...}.
        /// Tolerates whitespace and escaped quotes.
        /// 解析形如{"key1":"value1","key2":"value2",...}的扁平JSON对象。容忍空白和转义引号。
        /// </summary>
        private static Dictionary<string, string> ParseFlatJsonObject(string json)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            // Simple regex-based extraction (good enough for controlled i18n files).
            // 基于正则的简单提取（对受控i18n文件足够）。
            var regex = new Regex("\"([^\"]+)\"\\s*:\\s*\"((?:[^\\\\\"]|\\\\.)*)\"");
            var matches = regex.Matches(json);
            foreach (Match m in matches)
            {
                var key = Unescape(m.Groups[1].Value);
                var val = Unescape(m.Groups[2].Value);
                result[key] = val;
            }
            return result;
        }

        private static string Unescape(string s)
        {
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\t", "\t")
                    .Replace("\\r", "\r");
        }

        /// <summary>
        /// Translate a key, returning the key itself if no translation is found. Supports string.Format substitution.
        /// 翻译键，找不到翻译时返回键本身。支持string.Format替换。
        /// </summary>
        public static string T(string key, params object[] args)
        {
            EnsureInitialized();
            string text;
            if (_tables.TryGetValue(_currentLang, out var table) && table.TryGetValue(key, out text))
                return args.Length == 0 ? text : string.Format(text, args);
            if (_currentLang != FallbackLang && _tables.TryGetValue(FallbackLang, out var fb) && fb.TryGetValue(key, out text))
                return args.Length == 0 ? text : string.Format(text, args);
            return key;
        }
    }
}
