using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;
using Fosa.AvatarTextureOptimizer;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// JSON i18n. Discovers every *.json next to this file.
    /// Default Auto reads NDMF LanguagePrefs; missing keys fall back to en.
    /// JSON 本地化。会发现同目录下全部 json。Auto 读取 NDMF 语言，缺词回退英文。
    /// </summary>
    public static class AtoLoc
    {
        public const string DefaultLang = "en";
        const string Folder = "Packages/net.fosa.avatar-texture-optimizer/Editor/Localization";

        static Dictionary<string, Dictionary<string, string>> _tables;
        static string[] _available;
        static Localizer _ndmfLocalizer;

        public static Localizer NdmfLocalizer => _ndmfLocalizer ?? (_ndmfLocalizer = BuildNdmfLocalizer());

        public static IReadOnlyList<string> AvailableLanguages
        {
            get
            {
                EnsureLoaded();
                return _available;
            }
        }

        public static void Reload()
        {
            _tables = null;
            _available = null;
            _ndmfLocalizer = null;
        }

        public static string ResolveLanguage(AtoLanguageMode mode)
        {
            EnsureLoaded();
            if (mode == AtoLanguageMode.English) return Pick("en");
            if (mode == AtoLanguageMode.SimplifiedChinese) return Pick("zh-Hans", "zh-CN", "zh");
            // Auto
            var ndmf = LanguagePrefs.Language;
            if (!string.IsNullOrEmpty(ndmf))
            {
                var hit = Pick(ndmf, ndmf.Replace('_', '-'), ndmf.Split('-')[0]);
                if (hit != null) return hit;
            }

            return Pick(DefaultLang) ?? DefaultLang;
        }

        public static string T(AtoLanguageMode mode, string key)
        {
            EnsureLoaded();
            var lang = ResolveLanguage(mode);
            if (TryGet(lang, key, out var s)) return s;
            if (TryGet(DefaultLang, key, out s)) return s;
            return key;
        }

        public static string T(AtoLanguageMode mode, string key, params object[] args)
        {
            var fmt = T(mode, key);
            try
            {
                return string.Format(CultureInfo.InvariantCulture, fmt, args);
            }
            catch (FormatException)
            {
                return fmt;
            }
        }

        static bool TryGet(string lang, string key, out string value)
        {
            value = null;
            if (lang == null || _tables == null) return false;
            if (!_tables.TryGetValue(lang, out var table) || table == null) return false;
            return table.TryGetValue(key, out value) && value != null;
        }

        static string Pick(params string[] candidates)
        {
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                var n = Normalize(c);
                if (_tables.ContainsKey(n)) return n;
                foreach (var k in _tables.Keys)
                {
                    if (k.StartsWith(n, StringComparison.OrdinalIgnoreCase)) return k;
                    if (n.StartsWith(k, StringComparison.OrdinalIgnoreCase)) return k;
                }
            }

            return null;
        }

        static string Normalize(string lang)
        {
            if (string.Equals(lang, "zh-cn", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lang, "zh-hans", StringComparison.OrdinalIgnoreCase))
                return "zh-Hans";
            if (string.Equals(lang, "en-us", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lang, "en-gb", StringComparison.OrdinalIgnoreCase))
                return "en";
            return lang;
        }

        static void EnsureLoaded()
        {
            if (_tables != null) return;
            _tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();

            string[] files = Array.Empty<string>();
            if (Directory.Exists(Folder))
            {
                files = Directory.GetFiles(Folder, "*.json", SearchOption.TopDirectoryOnly);
            }
            else
            {
                // Fallback: locate next to this script. / 回退：按脚本目录查找。
                var script = AssetDatabase.FindAssets("t:MonoScript AtoLoc");
                foreach (var guid in script)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.Replace('\\', '/').EndsWith("/AtoLoc.cs", StringComparison.Ordinal))
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (dir != null && Directory.Exists(dir))
                            files = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
                        break;
                    }
                }
            }

            foreach (var file in files)
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var json = File.ReadAllText(file);
                    var table = ParseFlatJson(json);
                    var key = Normalize(name);
                    _tables[key] = table;
                    list.Add(key);
                    LanguagePrefs.RegisterLanguage(key);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[ATO] Failed to load i18n " + file + ": " + e.Message);
                }
            }

            list.Sort(StringComparer.OrdinalIgnoreCase);
            _available = list.ToArray();
        }

        /// <summary>
        /// Minimal parser for a flat {"key":"value"} JSON object. / 扁平 JSON 对象的最小解析器。
        /// </summary>
        static Dictionary<string, string> ParseFlatJson(string json)
        {
            var table = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(json)) return table;
            int i = 0;
            SkipWs();
            if (i >= json.Length || json[i] != '{') return table;
            i++;
            while (i < json.Length)
            {
                SkipWs();
                if (i < json.Length && json[i] == '}') break;
                if (i < json.Length && json[i] == ',') { i++; continue; }
                if (!TryReadString(out var key)) break;
                SkipWs();
                if (i >= json.Length || json[i] != ':') break;
                i++;
                SkipWs();
                if (!TryReadString(out var value)) break;
                table[key] = value;
            }

            return table;

            void SkipWs()
            {
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            }

            bool TryReadString(out string s)
            {
                s = null;
                SkipWs();
                if (i >= json.Length || json[i] != '"') return false;
                i++;
                var sb = new StringBuilder();
                while (i < json.Length)
                {
                    var c = json[i++];
                    if (c == '"') { s = sb.ToString(); return true; }
                    if (c != '\\' || i >= json.Length) { sb.Append(c); continue; }
                    var e = json[i++];
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
                            if (i + 4 <= json.Length &&
                                int.TryParse(json.Substring(i, 4), NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out var cp))
                            {
                                sb.Append((char)cp);
                                i += 4;
                            }
                            break;
                        default:
                            sb.Append(e);
                            break;
                    }
                }

                return false;
            }
        }

        static Localizer BuildNdmfLocalizer()
        {
            EnsureLoaded();
            return new Localizer(DefaultLang, () =>
            {
                var result = new List<(string, Func<string, string>)>();
                foreach (var kv in _tables)
                {
                    var table = kv.Value;
                    result.Add((kv.Key, k =>
                    {
                        if (table != null && table.TryGetValue(k, out var s)) return s;
                        return null;
                    }));
                }

                return result;
            });
        }
    }
}
