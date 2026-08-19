using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NetFosa.AvatarTextureOptimizer;
using UnityEditor;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.i18n
{
    /// <summary>
    /// ATO 本地化。读取 json 格式翻译文件（内置 en-US / zh-CN，用户可在 Assets 下任意
    /// "ATO_i18n" 目录中放更多语言文件，有多少个语言文件就显示多少个语言选项）。
    /// Auto 模式读取 NDMF 当前语言，缺翻译回退英文。
    /// </summary>
    public static class Localization
    {
        public struct LanguageInfo
        {
            public string code;        // e.g. "en-US"
            public string displayName; // native name shown in the dropdown
            public bool builtIn;
        }

        private static Dictionary<string, Dictionary<string, string>> _tables;
        private static List<LanguageInfo> _languages;
        private static bool _loaded;
        private static string _current;

        public static string CurrentCode => _current ?? "en-US";

        /// <summary>当前语言的原生名称（用于显示）。</summary>
        public static string CurrentLanguageName
        {
            get
            {
                EnsureLoaded();
                var info = _languages.FirstOrDefault(l => string.Equals(l.code, CurrentCode, StringComparison.OrdinalIgnoreCase));
                return info.displayName ?? "English";
            }
        }

        public static IReadOnlyList<LanguageInfo> AvailableLanguages
        {
            get { EnsureLoaded(); return _languages; }
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            _languages = new List<LanguageInfo>();

            // 1) 内置翻译（包内）
            LoadDirectory("Packages/net.fosa.avatar-texture-optimizer/Editor/i18n/Translations", true);
            // 2) 用户扩展（Assets 下任意 ATO_i18n 目录）
            foreach (var dir in Directory.GetDirectories(Application.dataPath, "ATO_i18n", SearchOption.AllDirectories))
            {
                LoadDirectory(ToAssetPath(dir), false);
            }

            _current = ResolveLanguage(ATOI18nLanguage.Auto);
            if (!_tables.ContainsKey(_current)) _current = "en-US";
        }

        private static void LoadDirectory(string assetDir, bool builtIn)
        {
            if (!AssetDatabase.IsValidFolder(assetDir)) return;
            foreach (var jsonPath in AssetDatabase.FindAssets("t:TextAsset", new[] { assetDir })
                         .Select(AssetDatabase.GUIDToAssetPath)
                         .Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(jsonPath);
                if (asset == null) continue;
                var code = Path.GetFileNameWithoutExtension(jsonPath);
                try
                {
                    var table = JsonUtility.FromJson<TranslationTable>(asset.text);
                    if (table == null || table.entries == null) continue;
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in table.entries) dict[e.key] = e.value;
                    _tables[code] = dict;
                    var display = builtIn ? BuiltInDisplayName(code) : code;
                    if (!_languages.Any(l => string.Equals(l.code, code, StringComparison.OrdinalIgnoreCase)))
                        _languages.Add(new LanguageInfo { code = code, displayName = display, builtIn = builtIn });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ATO] Failed to parse i18n file {jsonPath}: {ex.Message}");
                }
            }
        }

        private static string BuiltInDisplayName(string code)
        {
            switch (code.ToLowerInvariant())
            {
                case "en-us": return "English";
                case "zh-cn": return "简体中文";
                default: return code;
            }
        }

        private static string ToAssetPath(string absolute)
        {
            var dataPath = Application.dataPath.Replace('\\', '/');
            var rel = absolute.Replace('\\', '/');
            if (rel.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
                return "Assets" + rel.Substring(dataPath.Length);
            return rel;
        }

        /// <summary>根据用户语言代码解析最终语言（"" = Auto 跟随 NDMF）。</summary>
        public static string ResolveLanguage(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                // Auto
                try
                {
                    var ndmfLang = nadena.dev.ndmf.localization.LanguagePrefs.Language;
                    if (!string.IsNullOrEmpty(ndmfLang))
                    {
                        var mapped = MapNdmfCode(ndmfLang);
                        if (_tables.ContainsKey(mapped)) return mapped;
                    }
                }
                catch (Exception)
                {
                    // NDMF 不可用：忽略
                }
                return "en-US";
            }
            if (_tables.ContainsKey(code)) return code;
            return "en-US";
        }

        /// <summary>根据用户选项解析最终语言代码（枚举版本，兼容保留）。</summary>
        public static string ResolveLanguage(ATOI18nLanguage option)
        {
            switch (option)
            {
                case ATOI18nLanguage.English: return "en-US";
                case ATOI18nLanguage.SimplifiedChinese: return "zh-CN";
                default: return ResolveLanguage("");
            }
        }

        private static string MapNdmfCode(string code)
        {
            switch (code.ToLowerInvariant())
            {
                case "zh-hans":
                case "zh-cn":
                case "zh": return "zh-CN";
                default: return "en-US";
            }
        }

        public static void SetLanguage(string code)
        {
            EnsureLoaded();
            _current = ResolveLanguage(code);
        }

        /// <summary>取本地化文本；缺翻译回退英文再回退 key。</summary>
        public static string L(string key, params object[] args)
        {
            EnsureLoaded();
            string text = null;
            if (_tables.TryGetValue(_current, out var cur) && cur.TryGetValue(key, out text)) { }
            else if (_tables.TryGetValue("en-US", out var en) && en.TryGetValue(key, out text)) { }
            else text = key;

            if (args != null && args.Length > 0)
            {
                try { text = string.Format(text, args); }
                catch (FormatException) { }
            }
            return text;
        }

        /// <summary>供 Inspector 使用的辅助：从翻译表生成枚举选项显示名。</summary>
        public static string EnumLabel(string prefix, string enumName) => L($"{prefix}.{enumName}");
    }

    [Serializable]
    internal class TranslationTable
    {
        [Serializable]
        public class Entry
        {
            public string key;
            public string value;
        }

        public List<Entry> entries = new List<Entry>();
    }
}
