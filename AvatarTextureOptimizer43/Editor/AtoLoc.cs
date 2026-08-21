using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using nadena.dev.ndmf.localization;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// JSON i18n. Scans Localization/*.json so users can drop extra languages.
    /// Auto follows NDMF LanguagePrefs; missing keys fall back to en-US then the key.
    /// JSON 本地化。扫描 Localization 目录，用户可自行扩展语言。
    /// Auto 跟随 NDMF；缺翻译回退英文。
    /// </summary>
    public static class AtoLoc
    {
        const string RelDir = "Packages/net.fosa.avatar-texture-optimizer/Localization";

        static Dictionary<string, Dictionary<string, string>> _tables;
        static string[] _codes;
        static Localizer _ndmf;
        static string _overrideCode; // null = Auto

        public static Localizer NdmfLocalizer
        {
            get
            {
                EnsureLoaded();
                return _ndmf;
            }
        }

        public static IReadOnlyList<string> AvailableCodes
        {
            get
            {
                EnsureLoaded();
                return _codes;
            }
        }

        public static string CurrentCode
        {
            get
            {
                EnsureLoaded();
                if (!string.IsNullOrEmpty(_overrideCode)) return Normalize(_overrideCode);
                try { return Normalize(LanguagePrefs.Language); }
                catch { return "en-US"; }
            }
        }

        /// <summary>Set null for Auto. 设为 null 表示 Auto。</summary>
        public static void SetOverride(string code) => _overrideCode = code;

        public static void Reload()
        {
            _tables = null;
            _codes = null;
            _ndmf = null;
            EnsureLoaded();
        }

        public static string T(string key)
        {
            EnsureLoaded();
            var code = CurrentCode;
            if (TryGet(code, key, out var s)) return s;
            var bas = code.Split('-')[0];
            foreach (var c in _codes)
                if (c.StartsWith(bas, StringComparison.OrdinalIgnoreCase) && TryGet(c, key, out s))
                    return s;
            if (TryGet("en-US", key, out s)) return s;
            return key;
        }

        public static string T(string key, params object[] args)
        {
            try { return string.Format(T(key), args); }
            catch { return T(key); }
        }

        static bool TryGet(string code, string key, out string s)
        {
            s = null;
            if (_tables.TryGetValue(Normalize(code), out var t) && t.TryGetValue(key, out s) && !string.IsNullOrEmpty(s))
                return true;
            return false;
        }

        static void EnsureLoaded()
        {
            if (_tables != null) return;
            _tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();

            var dir = ResolveDir();
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var code = Path.GetFileNameWithoutExtension(file);
                        var json = File.ReadAllText(file);
                        var table = ParseFlatJson(json);
                        _tables[Normalize(code)] = table;
                        list.Add(Normalize(code));
                        LanguagePrefs.RegisterLanguage(Normalize(code));
                    }
                    catch (Exception e)
                    {
                        AtoLog.Warn("Failed to load i18n " + file + ": " + e.Message);
                    }
                }
            }

            if (list.Count == 0)
            {
                _tables["en-US"] = new Dictionary<string, string>();
                list.Add("en-US");
            }

            list.Sort(StringComparer.OrdinalIgnoreCase);
            _codes = list.ToArray();

            _ndmf = new Localizer("en-US", () =>
            {
                var result = new List<(string, Func<string, string>)>();
                foreach (var code in _codes)
                {
                    var captured = code;
                    result.Add((captured, k =>
                    {
                        if (_tables.TryGetValue(captured, out var t) && t.TryGetValue(k, out var v))
                            return v;
                        return null;
                    }));
                }
                return result;
            });
        }

        static string ResolveDir()
        {
            if (Directory.Exists(RelDir)) return RelDir;
            // Fallback: next to this source file's package. 回退：按脚本定位包根。
            var guids = AssetDatabase.FindAssets("AtoLoc t:MonoScript");
            foreach (var g in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                if (string.IsNullOrEmpty(path) || !path.Contains("AtoLoc")) continue;
                var pkg = path;
                // Editor/AtoLoc.cs -> package root
                var editor = Path.GetDirectoryName(pkg);
                var root = Path.GetDirectoryName(editor);
                var loc = Path.Combine(root ?? "", "Localization");
                if (Directory.Exists(loc)) return loc;
            }
            return RelDir;
        }

        static string Normalize(string code)
        {
            if (string.IsNullOrEmpty(code)) return "en-US";
            try { return CultureInfo.GetCultureInfo(code).Name; }
            catch
            {
                return code.Contains("-") ? code : code;
            }
        }

        /// <summary>
        /// Minimal flat JSON object parser {"k":"v", ...} supporting \n \t \" \\.
        /// 扁平 JSON 对象解析。
        /// </summary>
        public static Dictionary<string, string> ParseFlatJson(string json)
        {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(json)) return d;
            int i = 0;
            SkipWs(json, ref i);
            if (i >= json.Length || json[i] != '{') return d;
            i++;
            while (i < json.Length)
            {
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == '}') break;
                if (!TryReadString(json, ref i, out var key)) break;
                SkipWs(json, ref i);
                if (i >= json.Length || json[i] != ':') break;
                i++;
                SkipWs(json, ref i);
                if (!TryReadString(json, ref i, out var val)) break;
                d[key] = val;
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == ',') { i++; continue; }
                break;
            }
            return d;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static bool TryReadString(string s, ref int i, out string val)
        {
            val = null;
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '"') return false;
            i++;
            var sb = new System.Text.StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"') { val = sb.ToString(); return true; }
                if (c == '\\' && i < s.Length)
                {
                    var e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return false;
        }
    }
}
