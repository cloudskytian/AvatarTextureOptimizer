// User-extensible JSON i18n on top of NDMF Localizer.
// 基于 NDMF Localizer 的可扩展 JSON 本地化。
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Loads every "ATO_i18n_<code>.json" TextAsset found in the project (including the two
    /// shipped languages). Users add a json file to add a language. "Auto" follows NDMF's
    /// current language; missing keys fall back to English.
    /// 扫描项目内所有 "ATO_i18n_语言码.json"，有几个语言文件就显示几个语言；Auto 跟随 NDMF，
    /// 缺失键回退英文。
    /// </summary>
    public static class AtoL10n
    {
        public const string FilePrefix = "ATO_i18n_";
        private static Dictionary<string, Dictionary<string, string>> _tables;
        private static Localizer _localizer;

        /// <summary>Language override for UI ("" = Auto). / UI 语言覆盖（空 = Auto）。</summary>
        public static string LanguageOverride = "";

        public static Localizer Localizer
        {
            get { EnsureLoaded(); return _localizer; }
        }

        public static IReadOnlyList<string> AvailableLanguages
        {
            get { EnsureLoaded(); return _tables.Keys.OrderBy(x => x).ToList(); }
        }

        private static void EnsureLoaded()
        {
            if (_tables != null) return;
            _tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var guid in AssetDatabase.FindAssets(FilePrefix + " t:TextAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var file = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!file.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var code = file.Substring(FilePrefix.Length);
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (asset == null) continue;
                try
                {
                    var table = ParseFlatJson(asset.text);
                    if (_tables.TryGetValue(code, out var existing))
                        foreach (var kv in table) existing[kv.Key] = kv.Value; // user file may extend / 用户文件可扩展
                    else
                        _tables[code] = table;
                    AtoLog.Debugf($"i18n loaded: {code} ({table.Count} keys) from {path}");
                }
                catch (Exception e)
                {
                    AtoLog.Warn($"i18n file parse failed: {path}: {e.Message}");
                }
            }
            if (!_tables.ContainsKey("en-US")) _tables["en-US"] = new Dictionary<string, string>();

            _localizer = new Localizer("en-US", () =>
                _tables.Select(kv => ((string, Func<string, string>))(kv.Key,
                        k => kv.Value.TryGetValue(k, out var v) ? v : null))
                    .ToList());
        }

        /// <summary>Force re-scan (e.g. after user adds a language file). / 强制重扫语言文件。</summary>
        public static void Reload() { _tables = null; }

        /// <summary>Translate a key with current effective language. / 按当前有效语言取翻译。</summary>
        public static string Tr(string key)
        {
            EnsureLoaded();
            var lang = string.IsNullOrEmpty(LanguageOverride) ? LanguagePrefs.Language : LanguageOverride;
            if (TryLookup(lang, key, out var s)) return s;
            if (TryLookup("en-US", key, out s)) return s;
            return key;
        }

        public static string Tr(string key, params object[] args)
        {
            var fmt = Tr(key);
            try { return string.Format(fmt, args); }
            catch (FormatException) { return fmt; }
        }

        private static bool TryLookup(string lang, string key, out string value)
        {
            value = null;
            if (lang == null) return false;
            if (_tables.TryGetValue(lang, out var t) && t.TryGetValue(key, out value)) return true;
            // Region-less fallback e.g. zh-Hans -> zh / 地区码回退
            var dash = lang.IndexOf('-');
            if (dash > 0)
            {
                var baseLang = lang.Substring(0, dash);
                foreach (var kv in _tables)
                    if (kv.Key.StartsWith(baseLang, StringComparison.OrdinalIgnoreCase) &&
                        kv.Value.TryGetValue(key, out value)) return true;
            }
            return false;
        }

        /// <summary>Minimal flat string-to-string JSON parser (tolerant). / 简易扁平 JSON 解析。</summary>
        internal static Dictionary<string, string> ParseFlatJson(string json)
        {
            var dict = new Dictionary<string, string>();
            int i = 0;
            string ReadString()
            {
                var sb = new System.Text.StringBuilder();
                i++; // skip opening quote
                while (i < json.Length && json[i] != '"')
                {
                    if (json[i] == '\\' && i + 1 < json.Length)
                    {
                        i++;
                        switch (json[i])
                        {
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            case 'r': sb.Append('\r'); break;
                            case 'u':
                                if (i + 4 < json.Length &&
                                    ushort.TryParse(json.Substring(i + 1, 4),
                                        System.Globalization.NumberStyles.HexNumber, null, out var cp))
                                { sb.Append((char)cp); i += 4; }
                                break;
                            default: sb.Append(json[i]); break;
                        }
                    }
                    else sb.Append(json[i]);
                    i++;
                }
                i++; // closing quote
                return sb.ToString();
            }

            while (i < json.Length)
            {
                if (json[i] == '"')
                {
                    var key = ReadString();
                    while (i < json.Length && json[i] != ':') i++;
                    i++;
                    while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                    if (i < json.Length && json[i] == '"') dict[key] = ReadString();
                    else { while (i < json.Length && json[i] != ',' && json[i] != '}') i++; }
                }
                else i++;
            }
            return dict;
        }
    }
}
