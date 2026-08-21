using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Extensible i18n: scans all JSON files in Resources/i18n; each file = one language.
// 可扩展 i18n：扫描 Resources/i18n 下所有 JSON 文件，每个文件一种语言。

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// One loaded language file.
    /// 一个已加载的语言文件。
    /// </summary>
    public sealed class ATOLanguage
    {
        public string LanguageId;         // e.g. "en-US". 语言 ID。
        public string DisplayName;        // native name. 本地语言名。
        public Dictionary<string, string> Strings = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Lightweight localizer. In Auto mode it follows NDMF's current language (nadena.dev.ndmf.localization.LanguagePrefs.Language);
    /// any missing key falls back to English, then to the key itself.
    /// 轻量本地化器：Auto 模式跟随 NDMF 当前语言；缺失 key 回退英文，再回退 key 本身。
    /// </summary>
    public static class ATOLocalizer
    {
        private static List<ATOLanguage> _languages;
        private static ATOLanguage _active;

        /// <summary>
        /// Loads all languages from Resources/i18n. Returns false if none found.
        /// 从 Resources/i18n 加载全部语言；找不到返回 false。
        /// </summary>
        public static bool Load()
        {
            if (_languages != null) return _languages.Count > 0;
            _languages = new List<ATOLanguage>();
            var assets = Resources.LoadAll<TextAsset>("i18n");
            foreach (var asset in assets)
            {
                try
                {
                    var json = JsonUtility.FromJson<ATOLanguageJson>(asset.text);
                    if (string.IsNullOrEmpty(json.language)) continue;
                    var lang = new ATOLanguage { LanguageId = json.language, DisplayName = string.IsNullOrEmpty(json.displayName) ? json.language : json.displayName };
                    if (json.strings != null)
                        foreach (var kv in json.strings)
                            if (!string.IsNullOrEmpty(kv.key)) lang.Strings[kv.key] = kv.value;
                    _languages.Add(lang);
                }
                catch (Exception e)
                {
                    ATOLog.Warn($"i18n load failed for {asset.name}: {e.Message}");
                }
            }
            // Deterministic order. 确定性顺序。
            _languages = _languages.OrderBy(l => l.LanguageId, StringComparer.OrdinalIgnoreCase).ToList();
            return _languages.Count > 0;
        }

        public static IReadOnlyList<ATOLanguage> AvailableLanguages
        {
            get { Load(); return _languages; }
        }

        /// <summary>
        /// Selects the active language. mode=Auto follows NDMF's language; mode=Manual uses the given id.
        /// 选择活动语言：Auto 跟随 NDMF；Manual 使用指定 ID。
        /// </summary>
        public static void Select(ATOLanguageMode mode, string manualId)
        {
            Load();
            string wanted = null;
            if (mode == ATOLanguageMode.Manual)
            {
                wanted = manualId;
            }
            else
            {
                try
                {
                    // NDMF language id, e.g. "en-us" / "zh-hans". 读取 NDMF 当前语言。
                    wanted = nadena.dev.ndmf.localization.LanguagePrefs.Language;
                }
                catch (Exception)
                {
                    wanted = CultureInfo.CurrentCulture.Name;
                }
            }

            _active = _languages.FirstOrDefault(l => string.Equals(l.LanguageId, wanted, StringComparison.OrdinalIgnoreCase))
                   ?? _languages.FirstOrDefault(l => l.LanguageId.StartsWith(wanted?.Split('-')[0] ?? "en", StringComparison.OrdinalIgnoreCase))
                   ?? _languages.FirstOrDefault(l => l.LanguageId.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                   ?? _languages.FirstOrDefault();
        }

        /// <summary>
        /// Translates a key with optional {0},{1} substitution. Falls back to English, then the key.
        /// 翻译 key，支持 {0},{1} 占位符；缺失时回退英文，再回退 key。
        /// </summary>
        public static string T(string key, params object[] args)
        {
            if (_active == null) Select(ATOLanguageMode.Auto, null);
            if (_active == null) return key;
            if (!_active.Strings.TryGetValue(key, out var text))
            {
                var en = _languages.FirstOrDefault(l => l.LanguageId.StartsWith("en", StringComparison.OrdinalIgnoreCase));
                if (en != null && en.Strings.TryGetValue(key, out text)) { }
                else text = key;
            }
            try { return args.Length > 0 ? string.Format(text, args) : text; }
            catch (FormatException) { return text; }
        }

        [Serializable]
        private sealed class ATOLanguageJson
        {
            public string language;
            public string displayName;
            public List<StringPair> strings;
        }

        [Serializable]
        private sealed class StringPair
        {
            // Lowercase fields so hand-written JSON files use lowercase keys. 使用小写字段以便手写 JSON 使用小写 key。
            public string key;
            public string value;
        }
    }
}
