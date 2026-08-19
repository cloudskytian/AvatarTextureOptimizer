// AvatarTextureOptimizer
// File: Editor/Localization/ATOI18n.cs
//
// User-extensible i18n. All *.json files in the Localization folder of this
// package are loaded; every language with a config file becomes selectable in
// the UI. "Auto" follows NDMF's current language (LanguagePrefs.Language).
// Missing keys fall back to English.
//
// 用户可扩展的 i18n。本包 Localization 目录下的所有 *.json 配置文件都会被
// 加载；有几个语言的配置文件就显示几个语言。Auto 模式读取 NDMF 当前语言
// （LanguagePrefs.Language）。缺失的翻译回退到英文。
//
// Config file format (human friendly nested object):
//   { "locale": "en-US", "displayName": "English",
//     "strings": { "key": "value", ... } }
// A small purpose-built parser extracts the flat key/value map, so arbitrary
// extra top-level fields are tolerated. Invalid files are skipped with a
// warning instead of failing the build.
//
// 配置文件格式（人类友好的嵌套对象）：
//   { "locale": "en-US", "displayName": "English",
//     "strings": { "key": "value", ... } }
// 使用一个小型专用解析器提取扁平键值表，容忍任意额外顶层字段。非法文件
// 会被跳过并给出警告，而不是让构建失败。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.localization
{
    /// <summary>
    /// Immutable snapshot of one language file.
    /// 一个语言文件的不可变快照。
    /// </summary>
    public sealed class ATOLocale
    {
        public readonly string Locale;        // e.g. "en-US"
        public readonly string DisplayName;   // e.g. "English"
        private readonly Dictionary<string, string> _strings;

        public ATOLocale(string locale, string displayName, Dictionary<string, string> strings)
        {
            Locale = locale;
            DisplayName = displayName;
            _strings = strings;
        }

        /// <summary>Look up a key; returns null when missing. / 查找键，缺失返回 null。</summary>
        public string Get(string key)
        {
            return _strings.TryGetValue(key, out var v) ? v : null;
        }
    }

    /// <summary>
    /// Loads and resolves localized strings.
    /// 加载并解析本地化字符串。
    /// </summary>
    public static class ATOI18n
    {
        private const string LocalizationFolder = "Packages/net.fosa.avatar-texture-optimizer/Localization";
        private static List<ATOLocale> _locales;
        private static ATOLocale _englishFallback;

        /// <summary>
        /// All loaded locales. / 所有已加载的语言。
        /// </summary>
        public static IReadOnlyList<ATOLocale> Locales
        {
            get
            {
                if (_locales == null) Reload();
                return _locales;
            }
        }

        /// <summary>
        /// (Re)loads all JSON localization files from the Localization folder.
        /// Users can drop additional *.json files there to add languages.
        /// （重新）加载 Localization 目录下所有 JSON 本地化文件。用户可放入
        /// 额外的 *.json 文件以添加语言。
        /// </summary>
        public static void Reload()
        {
            _locales = new List<ATOLocale>();
            _englishFallback = null;

            string folder = LocalizationFolder;
            // Support plain-project layouts (Assets/...) as well as Package
            // layouts, so users who vendor the package can still use i18n.
            // 同时支持普通工程布局（Assets/...）与 Package 布局。
            if (!Directory.Exists(folder))
                folder = "Assets/AvatarTextureOptimizer/Localization";

            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(f => f))
                {
                    try
                    {
                        if (!MiniJson.TryParseObject(File.ReadAllText(file), out var root)) continue;
                        if (!root.TryGetValue("locale", out var locale) || string.IsNullOrEmpty(locale)) continue;

                        var display = root.TryGetValue("displayName", out var dn) ? dn : locale;
                        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (root.TryGetValue("strings", out var stringsSection))
                        {
                            var trimmed = stringsSection.Trim();
                            if (trimmed.Length > 1 && trimmed[0] == '{')
                            {
                                MiniJson.ParseObjectBody(trimmed, strings);
                            }
                        }

                        var loc = new ATOLocale(locale, display, strings);
                        _locales.Add(loc);
                        if (locale.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                            _englishFallback = loc;

                        // Register with NDMF so its language picker can show it.
                        // 注册到 NDMF，使其语言选择器可以显示该语言。
                        LanguagePrefs.RegisterLanguage(locale);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[ATO] Failed to load localization file {file}: {e.Message}");
                    }
                }
            }

            if (_englishFallback == null)
            {
                // English must always exist as the fallback; if the config is
                // missing, use an empty locale so lookups degrade gracefully.
                // 英文必须始终作为回退存在；配置文件缺失时使用空语言优雅降级。
                _englishFallback = new ATOLocale("en-US", "English", new Dictionary<string, string>());
            }
        }

        /// <summary>
        /// The currently active locale (Auto resolution against NDMF).
        /// 当前生效的语言（Auto 模式按 NDMF 解析）。
        /// </summary>
        public static ATOLocale ActiveLocale(string userSelection)
        {
            var locales = Locales;
            if (locales.Count == 0) return _englishFallback;

            string target;
            if (string.IsNullOrEmpty(userSelection) || userSelection == "Auto")
            {
                // Follow NDMF's current language. / 跟随 NDMF 当前语言。
                target = LanguagePrefs.Language; // e.g. "en-us", "zh-hans"
            }
            else
            {
                target = userSelection.ToLowerInvariant();
            }

            // Exact match. / 精确匹配。
            foreach (var l in locales)
                if (l.Locale.ToLowerInvariant() == target) return l;

            // Language-only prefix match (e.g. "zh" matches "zh-CN").
            // 仅语言部分前缀匹配（如 "zh" 匹配 "zh-CN"）。
            var langOnly = target.Split('-')[0];
            foreach (var l in locales)
                if (l.Locale.ToLowerInvariant().StartsWith(langOnly)) return l;

            // Fall back to English. / 回退到英文。
            return _englishFallback ?? locales[0];
        }

        /// <summary>
        /// Translate a key using the given locale; falls back to English, then
        /// to the key itself.
        /// 用给定语言翻译键；依次回退英文、键本身。
        /// </summary>
        public static string T(string key, ATOLocale locale = null, params object[] args)
        {
            var loc = locale ?? ActiveLocale(null);
            var s = loc.Get(key);
            if (s == null && !ReferenceEquals(loc, _englishFallback))
                s = _englishFallback?.Get(key);
            if (s == null) s = key;
            if (args != null && args.Length > 0)
            {
                try { s = string.Format(s, args); } catch (FormatException) { }
            }
            return s;
        }
    }

    /// <summary>
    /// A tiny JSON parser sufficient for localization files: objects with
    /// string values (plus the nested "strings" object). Handles escapes and
    /// tolerates whitespace. Not a general JSON parser — do not reuse for data.
    /// 一个足够用于本地化文件的小型 JSON 解析器：字符串值对象（含嵌套
    /// "strings" 对象）。处理转义并容忍空白。并非通用 JSON 解析器——不要
    /// 用于其他数据。
    /// </summary>
    internal static class MiniJson
    {
        public static bool TryParseObject(string text, out Dictionary<string, string> result)
        {
            result = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = 0;
            if (!SkipWs(text, ref i)) return false;
            if (i >= text.Length || text[i] != '{') return false;
            i++;
            return ParseObjectBody(text, result, ref i);
        }

        public static bool ParseObjectBody(string text, Dictionary<string, string> result)
        {
            int i = 0;
            return ParseObjectBody(text, result, ref i);
        }

        private static bool ParseObjectBody(string text, Dictionary<string, string> result, ref int i)
        {
            while (true)
            {
                if (!SkipWs(text, ref i)) return false;
                if (i >= text.Length) return false;
                if (text[i] == '}') { i++; return true; }
                if (text[i] == ',') { i++; continue; }

                if (text[i] != '"') return false;
                if (!ParseString(text, ref i, out var key)) return false;
                if (!SkipWs(text, ref i)) return false;
                if (i >= text.Length || text[i] != ':') return false;
                i++;
                if (!SkipWs(text, ref i)) return false;
                if (i >= text.Length) return false;

                if (text[i] == '"')
                {
                    if (!ParseString(text, ref i, out var value)) return false;
                    result[key] = value;
                }
                else if (text[i] == '{')
                {
                    // Nested object: recurse into it (used by "strings").
                    // 嵌套对象：递归解析（用于 "strings"）。
                    i++;
                    if (!ParseObjectBody(text, result, ref i)) return false;
                }
                else if (text[i] == '[')
                {
                    // Array: skip to the matching closing bracket.
                    // 数组：跳过到匹配的右括号。
                    if (!SkipArray(text, ref i)) return false;
                }
                else
                {
                    // Number / bool / null: skip token.
                    // 数字 / 布尔 / null：跳过该 token。
                    int start = i;
                    while (i < text.Length && text[i] != ',' && text[i] != '}') i++;
                    result[key] = text.Substring(start, i - start).Trim();
                }
            }
        }

        private static bool SkipArray(string text, ref int i)
        {
            int depth = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '"')
                {
                    if (!ParseString(text, ref i, out _)) return false;
                    continue;
                }
                if (c == '[') depth++;
                else if (c == ']') { depth--; if (depth == 0) { i++; return true; } }
                i++;
            }
            return false;
        }

        private static bool ParseString(string text, ref int i, out string value)
        {
            value = null;
            if (i >= text.Length || text[i] != '"') return false;
            i++;
            var sb = new StringBuilder();
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '"') { i++; value = sb.ToString(); return true; }
                if (c == '\\')
                {
                    i++;
                    if (i >= text.Length) return false;
                    char e = text[i];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 >= text.Length) return false;
                            var hex = text.Substring(i + 1, 4);
                            if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var cp))
                                sb.Append((char)cp);
                            i += 4;
                            break;
                        default: sb.Append(e); break;
                    }
                    i++;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return false; // unterminated string / 字符串未闭合
        }

        private static bool SkipWs(string text, ref int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            return i < text.Length;
        }
    }
}
