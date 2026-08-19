// ATOLocalization.cs
// JSON-file based i18n bridged to NDMF's Localizer / LanguagePrefs.
// 基于本地 JSON 文件的 i18n,桥接 NDMF Localizer / LanguagePrefs。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// Loads every *.json under the package Localization folder. One file = one language
    /// ("有几个语言的配置文件就显示几个语言"). Auto mode follows NDMF's language pref,
    /// falling back to English. / 枚举包内 Localization 目录的 json:一个文件即一种语言;
    /// Auto 跟随 NDMF 语言设置,缺翻译回退英文。
    /// </summary>
    internal static class ATOLocalization
    {
        internal const string FallbackLang = "en";

        private static Dictionary<string, Dictionary<string, string>> _locales; // lang(lower) -> (key -> text)
        private static List<string> _langDisplayOrder;

        /// <summary>NDMF localizer instance used for error reporting. / 用于错误报告的 NDMF Localizer。</summary>
        internal static Localizer Localizer { get; private set; }

        [InitializeOnLoadMethod]
        static void Init()
        {
            _locales = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            _langDisplayOrder = new List<string>();

            foreach (var file in EnumerateLocaleFiles())
            {
                var lang = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                var table = ParseFlatJson(File.ReadAllText(file));
                if (table.Count == 0) continue;
                _locales[lang] = table;
                if (!_langDisplayOrder.Contains(lang)) _langDisplayOrder.Add(lang);
            }

            // Ensure fallback exists / 确保回退语言存在
            if (!_locales.ContainsKey(FallbackLang))
                _locales[FallbackLang] = new Dictionary<string, string>();

            Localizer = new Localizer(FallbackLang, () =>
                _locales.Select(kv => (kv.Key, new Func<string, string>(k =>
                    kv.Value.TryGetValue(k, out var v) ? v : null))).ToList());
        }

        private static IEnumerable<string> EnumerateLocaleFiles()
        {
            // 1) Regular VPM/UPM package path / 常规包路径
            var dir = Path.Combine("Packages", PackageName).Replace('\\', '/');
            var locDir = Path.Combine(dir, "Localization");
            if (Directory.Exists(locDir))
            {
                foreach (var f in Directory.GetFiles(locDir, "*.json")) yield return f;
                yield break;
            }

            // 2) Copied into Assets (source install) / 源码拷贝进 Assets 的情况
            foreach (var guid in AssetDatabase.FindAssets($"t:TextAsset", new[] { "Assets" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (p == null || !p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Contains("AvatarTextureOptimizer/Localization") || p.Contains("avatar-texture-optimizer/Localization"))
                    yield return p;
            }
        }

        private const string PackageName = "net.fosa.avatar-texture-optimizer";

        /// <summary>All discovered language codes (lower case). / 全部已发现的语言代码(小写)。</summary>
        internal static IReadOnlyList<string> Languages => _langDisplayOrder;

        internal static string LangDisplayName(string code)
        {
            if (code == "en") return "English";
            if (code == "zh-hans") return "简体中文";
            return code;
        }

        /// <summary>Current UI language (auto = NDMF pref). / 当前界面语言(Auto=NDMF 设置)。</summary>
        internal static string CurrentLanguage =>
            ResolveLanguage(LanguagePrefs.Language);

        private static string ResolveLanguage(string requested)
        {
            if (string.IsNullOrEmpty(requested)) return FallbackLang;
            if (_locales.ContainsKey(requested)) return requested;
            var baseLang = requested.Split('-')[0];
            // try base-language match, e.g. "zh-hans" for "zh-Hans-CN" / 基础语言匹配
            var hit = _langDisplayOrder.FirstOrDefault(l => l == baseLang || l.Split('-')[0] == baseLang);
            return hit ?? FallbackLang;
        }

        /// <summary>Look up a localized string with {0} style formatting. / 查询本地化字符串并格式化。</summary>
        internal static string Tr(string key, params object[] args)
        {
            if (_locales == null) Init(); // lazy guard for early access / 早访问兜底
            if (Localizer != null && Localizer.TryGetLocalizedString(key, out var s)) return SafeFormat(s, args);
            var lang = CurrentLanguage;
            if (_locales.TryGetValue(lang, out var t) && t.TryGetValue(key, out var v)) return SafeFormat(v, args);
            if (_locales.TryGetValue(FallbackLang, out var t2) && t2.TryGetValue(key, out var v2)) return SafeFormat(v2, args);
            return key;
        }

        private static string SafeFormat(string s, object[] args)
        {
            if (args == null || args.Length == 0) return s;
            try { return string.Format(s, args); }
            catch { return s; }
        }

        // ------------------------------------------------------------------ //
        // Minimal flat JSON parser: {"k":"v", ...} — no nesting needed for i18n.
        // 极简扁平 JSON 解析器:i18n 只需 {"k":"v"} 平铺结构。
        // ------------------------------------------------------------------ //
        internal static Dictionary<string, string> ParseFlatJson(string text)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(text)) return result;
            int i = 0;
            SkipWs(text, ref i);
            if (i >= text.Length || text[i] != '{') return result;
            i++;
            while (true)
            {
                SkipWs(text, ref i);
                if (i >= text.Length) break;
                if (text[i] == '}') { i++; break; }
                if (text[i] == ',') { i++; continue; }
                if (text[i] != '"') break; // malformed / 格式错误
                var key = ParseString(text, ref i);
                SkipWs(text, ref i);
                if (i >= text.Length || text[i] != ':') break;
                i++;
                SkipWs(text, ref i);
                string value;
                if (i < text.Length && text[i] == '"') value = ParseString(text, ref i);
                else
                {
                    var start = i;
                    while (i < text.Length && text[i] != ',' && text[i] != '}') i++;
                    value = text.Substring(start, i - start).Trim();
                }
                result[key] = value;
            }
            return result;
        }

        private static void SkipWs(string t, ref int i)
        {
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
        }

        private static string ParseString(string t, ref int i)
        {
            i++; // opening quote / 开引号
            var sb = new System.Text.StringBuilder();
            while (i < t.Length)
            {
                var c = t[i];
                if (c == '\\')
                {
                    i++;
                    if (i >= t.Length) break;
                    switch (t[i])
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'u':
                            if (i + 4 < t.Length && ushort.TryParse(t.Substring(i + 1, 4),
                                    System.Globalization.NumberStyles.HexNumber, null, out var cp))
                            {
                                sb.Append((char)cp);
                                i += 4;
                            }
                            break;
                        default: sb.Append(t[i]); break;
                    }
                    i++;
                }
                else if (c == '"') { i++; break; }
                else { sb.Append(c); i++; }
            }
            return sb.ToString();
        }
    }
}
