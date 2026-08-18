// Avatar Texture Optimizer (ATO)
// User-extensible i18n: reads JSON localization files, shows as many languages as files
// exist, supports manual switching, Auto follows NDMF's language preference, and falls
// back to English when a translation is missing.
// 用户可扩展 i18n：读取 JSON 本地化文件，有多少语言文件就显示多少语言，支持手动切换，
// Auto 跟随 NDMF 当前语言配置，缺失翻译时回退到英文。

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace NetFosa.ATO
{
    [Serializable]
    internal class ATOI18nEntry { public string key; public string value; }

    [Serializable]
    internal class ATOI18nTable { public List<ATOI18nEntry> entries = new List<ATOI18nEntry>(); }

    /// <summary>
    /// Localization lookup. / 本地化查找。
    /// </summary>
    public static class ATOI18n
    {
        private static readonly Dictionary<string, Dictionary<string, string>> _tables
            = new Dictionary<string, Dictionary<string, string>>();

        private static string _current = "en";
        private static bool _initialized;

        public static IReadOnlyList<string> Languages => new List<string>(_tables.Keys);

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            // Locate JSON localization files by name. / 按文件名定位 JSON 本地化文件。
            var dirs = new[] { "Packages/net.fosa.avatar-texture-optimizer/Localization", "Assets" };
            foreach (var pattern in new[] { "ato_en", "ato_zh-hans", "ato_zh" })
            {
                foreach (var guid in AssetDatabase.FindAssets(pattern + " t:TextAsset", dirs))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    LoadFile(path);
                }
            }
            if (_tables.Count == 0)
            {
                // Fallback: try to load by relative path. / 兜底：按相对路径加载。
                TryLoadRelative("Localization/ato_en.json");
                TryLoadRelative("Localization/ato_zh-hans.json");
            }
        }

        private static void TryLoadRelative(string rel)
        {
            var full = Path.Combine(Application.dataPath, "..", rel);
            if (File.Exists(full)) LoadFile(rel);
        }

        private static void LoadFile(string path)
        {
            try
            {
                var ta = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                var json = ta != null ? ta.text : File.ReadAllText(path);
                var table = JsonUtility.FromJson<ATOI18nTable>(json);
                if (table == null) return;
                var lang = path.ToLowerInvariant().Contains("zh-hans") || path.ToLowerInvariant().Contains("zh_hans")
                    ? "zh-hans" : path.ToLowerInvariant().Contains("zh") ? "zh-hans" : "en";
                var dict = new Dictionary<string, string>();
                foreach (var e in table.entries)
                    if (!string.IsNullOrEmpty(e.key)) dict[e.key] = e.value;
                _tables[lang] = dict;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{ATOConstants.LogPrefix} Failed to load i18n file '{path}': {e.Message}");
            }
        }

        public static void SetLanguage(ATOLanguageMode mode)
        {
            Initialize();
            switch (mode)
            {
                case ATOLanguageMode.English: _current = "en"; break;
                case ATOLanguageMode.ChineseSimplified: _current = "zh-hans"; break;
                default:
                    var ndmfLang = nadena.dev.ndmf.localization.LanguagePrefs.Language;
                    _current = ndmfLang.StartsWith("zh") ? "zh-hans" : "en";
                    break;
            }
        }

        /// <summary>Translate a key for the currently selected language. / 用当前语言翻译键。</summary>
        public static string Tr(string key, params object[] args)
        {
            Initialize();
            var s = Lookup(_current, key) ?? Lookup("en", key) ?? key;
            try { return args.Length > 0 ? string.Format(s, args) : s; }
            catch { return s; }
        }

        /// <summary>Look up a key in a specific language (or null). / 在指定语言中查键（可能为 null）。</summary>
        public static string Lookup(string lang, string key)
        {
            Initialize();
            if (_tables.TryGetValue(lang, out var dict) && dict.TryGetValue(key, out var v)) return v;
            return null;
        }

        private static nadena.dev.ndmf.localization.Localizer _ndmfLocalizer;

        /// <summary>
        /// An NDMF Localizer backed by ATO's JSON tables, for error/report windows.
        /// 以 ATO JSON 表为后端的 NDMF Localizer，用于错误/报告窗口。
        /// </summary>
        public static nadena.dev.ndmf.localization.Localizer NdmfLocalizer
        {
            get
            {
                Initialize();
                if (_ndmfLocalizer == null)
                    _ndmfLocalizer = new nadena.dev.ndmf.localization.Localizer("en-us", () =>
                    {
                        var list = new List<(string, Func<string, string>)>
                        {
                            ("en-us", s => Lookup("en", s)),
                            ("zh-hans", s => Lookup("zh-hans", s)),
                        };
                        return list;
                    });
                return _ndmfLocalizer;
            }
        }
    }
}
