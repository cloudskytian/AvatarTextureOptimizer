// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Localization
{
    /// <summary>
    /// Extensible i18n: loads JSON localization files (one language per file) and exposes
    /// a language switch. Default "Auto" follows NDMF's current language; missing keys
    /// fall back to English. Users can drop extra language JSON files to add languages.
    ///
    /// 可扩展 i18n：加载 JSON 本地化文件（每种语言一个文件）并提供语言切换。
    /// 默认 Auto 跟随 NDMF 当前语言；缺失键回退英文。用户可放置更多语言 JSON 文件扩展。
    /// </summary>
    public static class ATOI18n
    {
        public const string DirPath = "Assets/Editor/Localization";

        private static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>();

        /// <summary>User-selected language (null = Auto). 用户选择的语言（null = Auto）。</summary>
        public static string OverrideLanguage;

        private static bool _loaded;

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            Tables.Clear();

            var dir = Path.Combine(Application.dataPath, "Editor", "Localization");
            if (!Directory.Exists(dir)) return;

            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    string lang = Path.GetFileNameWithoutExtension(file);
                    var json = File.ReadAllText(file);
                    var table = JsonUtility.FromJson<LocalizationFile>(json);
                    if (table?.entries != null)
                    {
                        var dict = new Dictionary<string, string>();
                        foreach (var e in table.entries)
                            if (!string.IsNullOrEmpty(e.key))
                                dict[e.key] = e.value;
                        Tables[lang] = dict;
                    }
                }
                catch (System.Exception e)
                {
                    ATOLog.Warning($"Failed to load i18n file {file}: {e.Message}");
                }
            }
        }

        public static IEnumerable<string> AvailableLanguages => Tables.Keys;

        /// <summary>Resolve the active language code. 解析当前语言代码。</summary>
        public static string ActiveLanguage
        {
            get
            {
                if (!string.IsNullOrEmpty(OverrideLanguage)) return OverrideLanguage;
                // Follow NDMF's language. 跟随 NDMF 语言。
                return LanguagePrefs.Language;
            }
        }

        /// <summary>Translate a key. 翻译一个键。</summary>
        public static string T(string key)
        {
            Load();
            if (Tables.TryGetValue(ActiveLanguage, out var table) && table.TryGetValue(key, out var v))
                return v;
            // Fallback to English. 回退英文。
            if (Tables.TryGetValue("en", out var en) && en.TryGetValue(key, out var ev))
                return ev;
            return key;
        }

        [System.Serializable]
        public class LocalizationFile
        {
            public List<Entry> entries;
        }

        [System.Serializable]
        public class Entry
        {
            public string key;
            public string value;
        }
    }
}
