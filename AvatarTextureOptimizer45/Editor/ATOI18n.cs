using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// ATO i18n 本地化系统 / ATO localization.
    ///
    /// 设计 / Design:
    ///  * 从 Editor/i18n/*.json 读取翻译, 有几个配置文件就支持几个语言 / Loads translations from
    ///    Editor/i18n/*.json; every JSON file present becomes a selectable language.
    ///  * 用户可自行添加语言文件(第三方也可扩展) / Users/third parties can add their own language files.
    ///  * 语言解析: 组件 language 设置(默认 Auto) -> Auto 时读取 NDMF 当前语言
    ///    (nadena.dev.ndmf.localization.LanguagePrefs.Language, 如 "zh-hans"), 并映射到可用文件;
    ///    无匹配则回退英文, 英文缺失则直接返回 key.
    ///    Resolution: component setting (default Auto) -> NDMF LanguagePrefs; falls back to English,
    ///    then to the key itself.
    /// </summary>
    internal static class ATOI18n
    {
        private const string I18nDir = "Packages/net.fosa.avatar-texture-optimizer/Editor/i18n";

        private static Dictionary<string, Dictionary<string, string>> _tables;
        private static List<string> _languages;
        private static string _current = "en";

        /// <summary>可用语言代码(源自文件) / Available language codes (from files).</summary>
        public static IReadOnlyList<string> Languages
        {
            get
            {
                EnsureLoaded();
                return _languages;
            }
        }

        /// <summary>当前语言 / Current language code.</summary>
        public static string CurrentLanguage
        {
            get
            {
                EnsureLoaded();
                return _current;
            }
        }

        /// <summary>
        /// 解析语言: 组件设置 -> NDMF 语言 -> 英文 / Resolve language: component setting -> NDMF -> English.
        /// </summary>
        public static void Resolve(string componentSetting)
        {
            EnsureLoaded();
            string want = "auto";
            if (!string.IsNullOrEmpty(componentSetting)) want = componentSetting;
            if (want.Equals("Auto", StringComparison.OrdinalIgnoreCase) || want.Equals("auto"))
            {
                want = ReadNdmfLanguage();
            }

            _current = MatchLanguage(want);
        }

        private static string ReadNdmfLanguage()
        {
            // 读取 NDMF 当前语言: "en-us"/"zh-hans"/"ja-jp"/... / Read NDMF's current language.
            try
            {
                var type = typeof(UnityEditor.Editor).Assembly
                    .GetType("nadena.dev.ndmf.localization.LanguagePrefs", false);
                if (type != null)
                {
                    var prop = type.GetProperty("Language");
                    var v = prop?.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ATO] 读取 NDMF 语言失败, 回退英文 / failed to read NDMF language: {e.Message}");
            }

            return "en";
        }

        private static string MatchLanguage(string want)
        {
            want = want.ToLowerInvariant().Replace('_', '-');
            if (_tables.ContainsKey(want)) return want;
            // "zh-hans" -> "zh-cn" 等近似匹配 / approximate matching (e.g. zh-hans -> zh-cn)
            string baseLang = want.Split('-')[0];
            var best = _languages.FirstOrDefault(l => l.StartsWith(baseLang));
            return best ?? "en";
        }

        /// <summary>取本地化文本 / Get localized text for a key.</summary>
        public static string T(string key)
        {
            EnsureLoaded();
            if (_tables.TryGetValue(_current, out var table) && table.TryGetValue(key, out var v)) return v;
            if (_tables.TryGetValue("en", out var en) && en.TryGetValue(key, out var v2)) return v2;
            return key;
        }

        /// <summary>取本地化文本并代入参数 / Get localized text with substitutions.</summary>
        public static string T(string key, params object[] args)
        {
            var s = T(key);
            try
            {
                return string.Format(s, args);
            }
            catch
            {
                return s;
            }
        }

        private static void EnsureLoaded()
        {
            if (_tables != null) return;
            _tables = new Dictionary<string, Dictionary<string, string>>();
            _languages = new List<string>();

            string dir = I18nDir;
            if (!Directory.Exists(dir))
            {
                // 包可能以其他形式存在(如本地包) / fall back when the package lives elsewhere
                var candidates = AssetDatabase.FindAssets("t:TextAsset").ToList()
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => p.Contains("/net.fosa.avatar-texture-optimizer/Editor/i18n/") && p.EndsWith(".json"));
                foreach (var p in candidates)
                {
                    string lang = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                    LoadFile(p, lang);
                }
            }
            else
            {
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    string lang = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    LoadFile(f, lang);
                }
            }

            if (!_tables.ContainsKey("en"))
            {
                // 兜底: 无文件时保证不崩 / fallback so missing files never crash
                _tables["en"] = new Dictionary<string, string>();
                _languages.Add("en");
            }

            _current = "en";
        }

        private static void LoadFile(string path, string lang)
        {
            try
            {
                string json = File.ReadAllText(path);
                var dict = JsonUtility.FromJson<AtoJsonFile>(json);
                var table = new Dictionary<string, string>();
                if (dict?.entries != null)
                {
                    foreach (var e in dict.entries)
                    {
                        if (e == null || string.IsNullOrEmpty(e.k)) continue;
                        table[e.k] = e.v;
                    }
                }

                _tables[lang] = table;
                if (!_languages.Contains(lang)) _languages.Add(lang);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ATO] 加载 i18n 文件失败 / failed to load i18n file {path}: {e.Message}");
            }
        }

        [Serializable]
        private class AtoJsonFile
        {
            public List<AtoEntry> entries;
        }

        [Serializable]
        private class AtoEntry
        {
            public string k;
            public string v;
        }
    }
}
