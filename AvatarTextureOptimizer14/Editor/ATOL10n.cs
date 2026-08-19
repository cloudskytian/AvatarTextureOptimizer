// ATOL10n — user-extensible JSON i18n / 用户可扩展的 JSON 本地化
// Mechanism follows NDMF/MA conventions (verified in their sources): flat {"key": "text"} json files,
// registered languages with NDMF LanguagePrefs; "Auto" follows NDMF's current language, missing keys fall back to en-US.<br>
// 机制与 NDMF/MA 源码一致：扁平 {"key":"text"} JSON；语言注册进 NDMF LanguagePrefs；
// "Auto" 跟随 NDMF 当前语言；缺失键回退英文。用户可在 i18n/ 目录加 json 扩展语言（有几个显示几个）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    internal static class ATOL10n
    {
        public const string Fallback = "en-US";

        // Folder GUID is stable: it is shipped inside the package. / 目录 GUID 随包发布保持稳定
        // (points to i18n/; generated on first dev import, see ATOi18nFolderAnchor) — we search by name instead
        // to survive guid churn: / 为抗 GUID 漂移，直接按包名+目录名查找。
        private static string _i18nRoot;
        private static Dictionary<string, Dictionary<string, string>> _tables; // lang -> (key -> text)
        private static List<string> _languages;
        private static string _overrideLanguage; // "Auto" or a culture code / "Auto" 或具体语言码
        private static Localizer _localizer;

        /// <summary>NDMF localizer over our tables (used for NDMF console reports). / 供 NDMF 控制台报告使用的 Localizer。</summary>
        internal static Localizer L
        {
            get
            {
                EnsureLoaded();
                if (_localizer == null)
                {
                    _localizer = new Localizer(Fallback, () => _tables
                        .Select(kv => (kv.Key, (Func<string, string>)(k => kv.Value.TryGetValue(k, out var v) ? v : null)))
                        .ToList());
                }
                return _localizer;
            }
        }

        internal static IReadOnlyList<string> Languages => EnsureLoaded() ? _languages : null;

        private static string I18nRoot()
        {
            if (_i18nRoot != null) return _i18nRoot;
            // Locate this package by finding our asmdef asset. / 通过 asmdef 资产定位本包目录
            foreach (var guid in AssetDatabase.FindAssets("t:asmdef net.fosa.avatar-texture-optimizer"))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (!p.EndsWith("net.fosa.avatar-texture-optimizer.asmdef", StringComparison.OrdinalIgnoreCase)) continue;
                var pkgRoot = Path.GetDirectoryName(p);
                var root = pkgRoot?.Replace('\\', '/');
                var candidate = root + "/i18n";
                if (Directory.Exists(candidate)) { _i18nRoot = candidate; return _i18nRoot; }
                // Editor asmdef lives under Editor/ when copied manually / 手动拷贝时 asmdef 在 Editor 下
                candidate = Path.GetDirectoryName(root) + "/i18n";
                if (Directory.Exists(candidate)) { _i18nRoot = candidate.Replace('\\', '/'); return _i18nRoot; }
            }
            _i18nRoot = "Packages/net.fosa.avatar-texture-optimizer/i18n";
            return _i18nRoot;
        }

        private static bool EnsureLoaded()
        {
            if (_tables != null) return true;
            _tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var root = I18nRoot();
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.GetFiles(root, "*.json"))
                {
                    try
                    {
                        var lang = Path.GetFileNameWithoutExtension(file);
                        var dict = MiniJson.ParseStringDict(File.ReadAllText(file));
                        if (dict != null)
                        {
                            // Normalize culture code (e.g. zh-cn -> zh-CN) / 规范化语言码
                            try { lang = CultureInfo.GetCultureInfo(lang).Name; } catch { /* keep raw / 保持原始 */ }
                            _tables[lang] = dict;
                        }
                    }
                    catch (Exception e) { ATOLog.Warn($"Failed to load i18n file '{file}': {e.Message}"); }
                }
            }
            if (!_tables.ContainsKey(Fallback)) _tables[Fallback] = new Dictionary<string, string>();
            _languages = _tables.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            foreach (var lang in _languages) { try { LanguagePrefs.RegisterLanguage(lang); } catch { /* NDMF present in any case / NDMF 必然存在但保守容错 */ } }
            return true;
        }

        /// <summary>Manual language override ("Auto" follows NDMF). / 手动语言覆盖（"Auto" 表示跟随 NDMF）。</summary>
        internal static string OverrideLanguage
        {
            get => _overrideLanguage ?? "Auto";
            set { _overrideLanguage = string.IsNullOrEmpty(value) ? "Auto" : value; }
        }

        private static string CurrentLanguage()
        {
            EnsureLoaded();
            if (!string.Equals(OverrideLanguage, "Auto", StringComparison.OrdinalIgnoreCase)) return OverrideLanguage;
            try { return LanguagePrefs.Language; } catch { return Fallback; } // 默认 Auto 读取 NDMF 当前语言
        }

        /// <summary>Translate with {0}-style substitution; falls back to en-US then to the key itself. / 翻译（支持 {0} 替换），回退英文/键名。</summary>
        internal static string T(string key, params object[] args)
        {
            EnsureLoaded();
            var cur = CurrentLanguage();
            string s = null;
            if (cur != null && _tables.TryGetValue(cur, out var t) && t.TryGetValue(key, out s)) { }
            else
            {
                var baseLang = cur?.Split('-')[0];
                var kv = baseLang == null ? null : _tables
                    .Where(p => p.Key.Equals(baseLang, StringComparison.OrdinalIgnoreCase) ||
                                p.Key.StartsWith(baseLang + "-", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Value).FirstOrDefault(d => d.ContainsKey(key));
                if (kv != null) s = kv.TryGetValue(key, out var v) ? v : null;
                if (s == null) _tables[Fallback].TryGetValue(key, out s);
            }
            s = s ?? ("<" + key + ">");
            return args != null && args.Length > 0 ? string.Format(s, args) : s;
        }

        /// <summary>Register an additional language table at runtime (3rd-party extension point). / 供第三方运行时注册语言表。</summary>
        public static void RegisterLanguageTable(string langCode, Dictionary<string, string> table)
        {
            EnsureLoaded();
            if (table == null) return;
            _tables[langCode] = table;
            _languages = _tables.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        }

        /// <summary>Minimal JSON parser for flat string dictionaries (avoids external deps). / 极简 JSON 解析（扁平字典，零依赖）。</summary>
        private static class MiniJson
        {
            internal static Dictionary<string, string> ParseStringDict(string json)
            {
                var result = new Dictionary<string, string>();
                var i = 0; var n = json.Length;
                void SkipWs() { while (i < n && char.IsWhiteSpace(json[i])) i++; }
                string ParseString()
                {
                    if (i >= n || json[i] != '"') return null;
                    i++; var sb = new System.Text.StringBuilder();
                    while (i < n)
                    {
                        var c = json[i++];
                        if (c == '"') return sb.ToString();
                        if (c == '\\' && i < n)
                        {
                            var e = json[i++];
                            switch (e)
                            {
                                case '"': sb.Append('"'); break; case '\\': sb.Append('\\'); break; case '/': sb.Append('/'); break;
                                case 'n': sb.Append('\n'); break; case 'r': sb.Append('\r'); break; case 't': sb.Append('\t'); break;
                                case 'b': sb.Append('\b'); break; case 'f': sb.Append('\f'); break;
                                case 'u':
                                    if (i + 4 <= n) { sb.Append((char)Convert.ToInt32(json.Substring(i, 4), 16)); i += 4; }
                                    break;
                            }
                        }
                        else sb.Append(c);
                    }
                    return null;
                }
                SkipWs(); if (i >= n || json[i] != '{') return null; i++;
                while (true)
                {
                    SkipWs(); if (i >= n) return null;
                    if (json[i] == '}') { i++; break; }
                    if (json[i] == ',') { i++; continue; }
                    var key = ParseString(); if (key == null) return null;
                    SkipWs(); if (i >= n || json[i] != ':') return null; i++;
                    SkipWs();
                    string value;
                    if (json[i] == '"') value = ParseString();
                    else { var st = i; while (i < n && json[i] != ',' && json[i] != '}') i++; value = json.Substring(st, i - st).Trim(); }
                    result[key] = value;
                }
                return result;
            }
        }
    }
}
