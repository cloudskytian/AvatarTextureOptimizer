// ATOI18n.cs
// Internationalization system. Loads JSON translation files, defaults to Auto
// (reads NDMF's current language), falls back to English.
// 国际化系统。加载 JSON 翻译文件，默认 Auto（读取 NDMF 当前语言），回退到英文。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Handles internationalization for ATO. Reads JSON config files from the i18n folder.
    /// Supports user-extensible translations.
    /// ATO 的国际化处理。
    /// </summary>
    internal static class ATOI18n
    {
        private static Dictionary<string, Dictionary<string, string>> _translations;
        private static string _currentLanguage = "auto";

        internal static event Action OnLanguageChanged;

        internal static string CurrentLanguage
        {
            get => _currentLanguage;
            set
            {
                _currentLanguage = value;
                OnLanguageChanged?.Invoke();
            }
        }

        internal static List<string> AvailableLanguages
        {
            get
            {
                EnsureLoaded();
                var langs = new List<string>(_translations.Keys);
                langs.Sort();
                return langs;
            }
        }

        internal static void EnsureLoaded()
        {
            if (_translations != null) return;
            _translations = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            // Load built-in translations from the i18n folder
            var i18nPath = GetI18nFolderPath();
            if (Directory.Exists(i18nPath))
            {
                foreach (var file in Directory.GetFiles(i18nPath, "*.json"))
                {
                    var langCode = Path.GetFileNameWithoutExtension(file);
                    var dict = LoadTranslationFile(file);
                    if (dict != null)
                        _translations[langCode] = dict;
                }
            }

            // Also scan for user-provided translations in the project
            var userPath = Path.Combine(Application.persistentDataPath, "ATO", "i18n");
            if (Directory.Exists(userPath))
            {
                foreach (var file in Directory.GetFiles(userPath, "*.json"))
                {
                    var langCode = Path.GetFileNameWithoutExtension(file);
                    var dict = LoadTranslationFile(file);
                    if (dict != null)
                        _translations[langCode] = dict;
                }
            }
        }

        private static string GetI18nFolderPath()
        {
            // Find the ATO package path
            var guids = AssetDatabase.FindAssets("ATOI18n");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var dir = Path.GetDirectoryName(path);
                if (Directory.Exists(Path.Combine(dir, "..", "i18n")))
                    return Path.Combine(dir, "..", "i18n").Replace('\\', '/');
            }
            // Fallback: relative to this file's location
            return "Packages/net.fosa.avatar-texture-optimizer/Editor/i18n";
        }

        private static Dictionary<string, string> LoadTranslationFile(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                return ParseSimpleJSON(json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ATO] Failed to load i18n file {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Simple JSON parser for flat key-value translation files.
        /// 简单 JSON 解析器，用于扁平键值翻译文件。
        /// </summary>
        private static Dictionary<string, string> ParseSimpleJSON(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var parsed = JsonUtility.FromJson<TranslationData>(json);
                if (parsed?.entries != null)
                {
                    foreach (var entry in parsed.entries)
                        result[entry.key] = entry.value;
                }
            }
            catch
            {
                // Fallback: manual line parsing for simple {"key": "value"} format
                // This handles standard JSON objects
                var lines = json.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim().TrimEnd(',');
                    if (!trimmed.StartsWith("\"")) continue;
                    var colonIdx = trimmed.IndexOf("\":");
                    if (colonIdx < 0) continue;
                    var key = trimmed.Substring(1, colonIdx - 1);
                    var value = trimmed.Substring(colonIdx + 2).Trim().Trim('"');
                    result[key] = value;
                }
            }
            return result;
        }

        [Serializable]
        private class TranslationData
        {
            public TranslationEntry[] entries;
        }

        [Serializable]
        private class TranslationEntry
        {
            public string key;
            public string value;
        }

        /// <summary>
        /// Translates a key to the current language, falling back to English, then to the key itself.
        /// 将键翻译为当前语言，回退到英文，再回退到键本身。
        /// </summary>
        internal static string T(string key)
        {
            EnsureLoaded();

            string lang = _currentLanguage;
            if (lang == "auto")
            {
                // Read NDMF's current language
                lang = GetNDMFLanguage();
            }

            if (_translations.TryGetValue(lang, out var dict) && dict.TryGetValue(key, out var val))
                return val;

            // Fallback to English
            if (lang != "en" && _translations.TryGetValue("en", out var enDict) &&
                enDict.TryGetValue(key, out var enVal))
                return enVal;

            // Fallback to key itself
            return key;
        }

        private static string GetNDMFLanguage()
        {
            try
            {
                // Attempt to read NDMF's LanguagePrefs
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => { try { return a.GetType("nadena.dev.ndmf.localization.LanguagePrefs"); } catch { return null; } })
                    .FirstOrDefault(t => t != null);
                if (type != null)
                {
                    var prop = type.GetProperty("Language", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (prop != null)
                        return (string)prop.GetValue(null);
                }
            }
            catch { }
            return "en";
        }

        internal static string GetLanguageDisplayName(string langCode)
        {
            return langCode switch
            {
                "auto" => "Auto (Follow NDMF)",
                "en" => "English",
                "zh-CN" => "简体中文",
                "ja" => "日本語",
                "ko" => "한국어",
                _ => langCode
            };
        }
    }
}
