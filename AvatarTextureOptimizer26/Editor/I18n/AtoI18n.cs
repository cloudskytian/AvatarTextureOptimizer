using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// JSON-driven i18n. Auto follows NDMF LanguagePrefs; missing keys fall back to en-us.
    /// 基于 JSON 的 i18n。Auto 跟随 NDMF；缺 key 回退英文。
    /// Parser is dependency-free (no Newtonsoft). / 解析器无第三方依赖。
    /// </summary>
    public static class AtoI18n
    {
        public const string PackageRoot = "Packages/net.fosa.avatar-texture-optimizer";
        public const string LangFolder = PackageRoot + "/Editor/I18n/Languages";

        private static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private static Localizer _ndmfLocalizer;
        private static string _forcedLanguage;
        private static bool _loaded;

        public static Localizer NdmfLocalizer
        {
            get
            {
                EnsureLoaded();
                return _ndmfLocalizer;
            }
        }

        public static IReadOnlyList<string> AvailableLanguages
        {
            get
            {
                EnsureLoaded();
                return new List<string>(Tables.Keys);
            }
        }

        public static string CurrentLanguage
        {
            get
            {
                if (!string.IsNullOrEmpty(_forcedLanguage)) return _forcedLanguage;
                try { return LanguagePrefs.Language ?? "en-us"; }
                catch { return "en-us"; }
            }
        }

        public static void SetForcedLanguage(string bcp47OrNull)
        {
            _forcedLanguage = string.IsNullOrEmpty(bcp47OrNull) ? null : bcp47OrNull.ToLowerInvariant();
        }

        public static void Reload()
        {
            _loaded = false;
            Tables.Clear();
            EnsureLoaded();
        }

        public static string T(string key)
        {
            EnsureLoaded();
            var lang = Normalize(CurrentLanguage);
            if (TryGet(lang, key, out var v)) return v;
            var dash = lang.IndexOf('-');
            if (dash > 0 && TryGet(lang.Substring(0, dash), key, out v)) return v;
            if (TryGet("en-us", key, out v)) return v;
            if (TryGet("en", key, out v)) return v;
            return key;
        }

        public static string Tf(string key, params object[] args)
        {
            try { return string.Format(T(key), args); }
            catch { return T(key); }
        }

        private static bool TryGet(string lang, string key, out string value)
        {
            value = null;
            if (!Tables.TryGetValue(Normalize(lang), out var table)) return false;
            return table.TryGetValue(key, out value);
        }

        internal static string Normalize(string lang)
        {
            if (string.IsNullOrEmpty(lang)) return "en-us";
            return lang.Trim().Replace('_', '-').ToLowerInvariant();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            var folder = ResolveLangFolder();
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder, "*.json"))
                    LoadFile(file);
            }

            if (!Tables.ContainsKey("en-us"))
                Tables["en-us"] = new Dictionary<string, string>();

            _ndmfLocalizer = new Localizer("en-us", () =>
            {
                var list = new List<(string, Func<string, string>)>();
                foreach (var kv in Tables)
                {
                    var table = kv.Value;
                    list.Add((kv.Key, k => table.TryGetValue(k, out var s) ? s : null));
                }
                return list;
            });
        }

        private static string ResolveLangFolder()
        {
            try
            {
                var p = Path.GetFullPath(LangFolder);
                if (Directory.Exists(p)) return p;
            }
            catch { /* ignore */ }

            var guids = AssetDatabase.FindAssets("t:TextAsset");
            foreach (var g in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                var n = path.Replace('\\', '/');
                if (n.EndsWith("/I18n/Languages/en-us.json", StringComparison.OrdinalIgnoreCase) ||
                    n.EndsWith("/I18n/Languages/en-US.json", StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(Path.GetFullPath(path));
            }
            return LangFolder;
        }

        private static void LoadFile(string path)
        {
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                ParseLangFile(json, Path.GetFileNameWithoutExtension(path), out var lang, out var table);
                lang = Normalize(lang);
                Tables[lang] = table;
                try { LanguagePrefs.RegisterLanguage(lang); } catch { /* NDMF optional */ }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{AtoLog.Prefix} Failed to load i18n {path}: {e.Message}");
            }
        }

        /// <summary>
        /// Minimal JSON object parser for { "language": "...", "strings": { "k": "v" } }.
        /// 仅解析本包 i18n 文件结构的迷你 JSON 解析器。
        /// </summary>
        internal static void ParseLangFile(string json, string fallbackLang,
            out string lang, out Dictionary<string, string> table)
        {
            lang = fallbackLang;
            table = new Dictionary<string, string>();
            var langMatch = Regex.Match(json, "\"language\"\\s*:\\s*\"([^\"]+)\"");
            if (langMatch.Success) lang = langMatch.Groups[1].Value;

            var stringsMatch = Regex.Match(json, "\"strings\"\\s*:\\s*\\{");
            if (!stringsMatch.Success) return;
            var i = stringsMatch.Index + stringsMatch.Length;
            while (i < json.Length)
            {
                SkipWs(json, ref i);
                if (i >= json.Length || json[i] == '}') break;
                if (json[i] == ',') { i++; continue; }
                if (json[i] != '"') break;
                var key = ReadJsonString(json, ref i);
                SkipWs(json, ref i);
                if (i >= json.Length || json[i] != ':') break;
                i++;
                SkipWs(json, ref i);
                if (i >= json.Length || json[i] != '"') break;
                var val = ReadJsonString(json, ref i);
                table[key] = val;
            }
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static string ReadJsonString(string s, ref int i)
        {
            if (s[i] != '"') return "";
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    var e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                var hex = s.Substring(i, 4);
                                if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var cp))
                                    sb.Append((char)cp);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
