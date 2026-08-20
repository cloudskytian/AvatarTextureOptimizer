using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Fosa.Ato.Editor.i18n
{
    /// <summary>
    /// Extensible localization. Language files are JSON files at `Assets/.../Resources/i18n/ato-*.json`
    /// AND bundled `Fosa/Ato/i18n/*.json`. Each additional file automatically becomes a selectable
    /// language. Default is Auto (reads NDMF language), fallback English.
    /// 可扩展本地化：读取现有 json 配置文件；有几个语言配置文件就显示几个语言；默认 Auto 读取 NDMF
    /// 当前语言，缺失翻译回退英文。
    /// </summary>
    internal static class Localizer
    {
        private const string ResourceDir = "i18n";
        private const string Prefix = "ato-";
        private static readonly Dictionary<string, Dictionary<string, string>> Langs = new();
        private static string _current = "auto";
        private static string _resolved = "en";

        public static string CurrentLanguage
        {
            get => _current;
            set
            {
                _current = string.IsNullOrEmpty(value) ? "auto" : value;
                EditorPrefs.SetString("ATO.Lang", _current);
                Resolve();
            }
        }

        public static IReadOnlyList<string> AvailableLanguages => Langs.Keys.OrderBy(k => k).ToList();

        static Localizer()
        {
            _current = EditorPrefs.GetString("ATO.Lang", "auto");
            Reload();
        }

        public static void Reload()
        {
            Langs.Clear();
            LoadBundled();
            LoadUserFiles();
            Resolve();
        }

        private static void LoadBundled()
        {
            // Load from Resources via TextAsset (works in packaged UPM after .meta exists).
            // 通过 Resources 加载（打包成 UPM 且有 .meta 后可用）。
            foreach (var ta in Resources.LoadAll<TextAsset>(ResourceDir))
            {
                if (ta != null && ta.name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string lang = ta.name.Substring(Prefix.Length).ToLowerInvariant();
                    Merge(lang, ta.text);
                }
            }
        }

        private static void LoadUserFiles()
        {
            // Also scan a user-writable folder so third parties can drop in translations.
            // 扫描用户可写目录，方便第三方添加翻译。
            var dirs = new[]
            {
                Path.Combine(Application.dataPath, "AvatarTextureOptimizer", "i18n"),
                Path.Combine(Application.dataPath, "Fosa", "ATO", "i18n"),
            };
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    if (!name.StartsWith(Prefix)) continue;
                    var lang = name.Substring(Prefix.Length);
                    try { Merge(lang, File.ReadAllText(f)); }
                    catch (Exception e) { AtoLog.Warn($"Failed to load i18n file {f}: {e.Message}"); }
                }
            }
        }

        private static void Merge(string lang, string json)
        {
            if (!Langs.TryGetValue(lang, out var dict))
            {
                dict = new Dictionary<string, string>(StringComparer.Ordinal);
                Langs[lang] = dict;
            }
            // Minimal JSON parser for flat { "key": "value" } objects.
            // 极简 JSON 解析器，仅支持扁平键值对。
            int i = 0;
            SkipWs(json, ref i);
            if (i >= json.Length || json[i] != '{') return;
            i++;
            while (i < json.Length)
            {
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == '}') break;
                string key = ReadString(json, ref i);
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == ':') i++;
                string val = ReadString(json, ref i);
                if (key != null) dict[key] = val ?? "";
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == ',') { i++; continue; }
            }
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static string ReadString(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            var sb = new System.Text.StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        case '/': sb.Append('/'); break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string DetectNdmfLanguage()
        {
            try
            {
                // NDMF stores language in EditorPrefs; reflect to avoid hard dependency on internals.
                // NDMF 把语言存在 EditorPrefs，用反射读取以免硬依赖内部实现。
                var t = Type.GetType("nadena.dev.ndmf.localization.LanguagePref, nadena.dev.ndmf");
                if (t != null)
                {
                    var v = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    if (v is string s && !string.IsNullOrEmpty(s)) return Normalize(s);
                }
            }
            catch { }
            // Fallback: system language
            return Application.systemLanguage switch
            {
                SystemLanguage.Chinese => "zh-hans",
                SystemLanguage.ChineseSimplified => "zh-hans",
                SystemLanguage.ChineseTraditional => "zh-hant",
                SystemLanguage.Japanese => "ja",
                _ => "en",
            };
        }

        private static string Normalize(string lang)
        {
            if (string.IsNullOrEmpty(lang)) return "en";
            lang = lang.ToLowerInvariant();
            if (lang.StartsWith("zh")) return lang.Contains("hant") || lang.Contains("tw") || lang.Contains("hk") ? "zh-hant" : "zh-hans";
            if (lang.StartsWith("ja")) return "ja";
            if (lang.Contains("-")) lang = lang.Split('-')[0];
            return lang;
        }

        private static void Resolve()
        {
            string want = _current == "auto" ? DetectNdmfLanguage() : _current;
            if (Langs.ContainsKey(want)) _resolved = want;
            else if (Langs.ContainsKey("en")) _resolved = "en";
            else _resolved = Langs.Keys.FirstOrDefault() ?? "en";
        }

        /// <summary>Translate a key with optional {0} substitutions. / 翻译，支持 {0} 占位。</summary>
        public static string T(string key, params object[] args)
        {
            string val;
            if (Langs.TryGetValue(_resolved, out var dict) && dict.TryGetValue(key, out val)) { }
            else if (Langs.TryGetValue("en", out var end) && end.TryGetValue(key, out val)) { }
            else val = key;
            if (args != null && args.Length > 0)
            {
                try { val = string.Format(val, args); } catch { }
            }
            return val;
        }

        public static GUIContent Gui(string key, string tooltipKey = null, params object[] args)
        {
            var text = T(key, args);
            var tip = tooltipKey != null ? T(tooltipKey) : null;
            return string.IsNullOrEmpty(tip) ? new GUIContent(text) : new GUIContent(text, tip);
        }
    }
}
