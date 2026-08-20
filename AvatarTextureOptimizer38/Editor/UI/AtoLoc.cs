using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// JSON i18n. Discovers every *.json in Editor/i18n. Auto follows NDMF LanguagePrefs.
    /// JSON 本地化。扫描 Editor/i18n 下全部 json。Auto 跟随 NDMF 语言，缺失回退英文。
    /// </summary>
    public static class AtoLoc
    {
        public const string PackageRoot = "Packages/net.fosa.avatar-texture-optimizer";
        public const string I18nFolder = PackageRoot + "/Editor/i18n";

        private static Dictionary<string, Dictionary<string, string>> _tables;
        private static Localizer _ndmfLocalizer;
        private static bool _loaded;

        public static Localizer NdmfLocalizer
        {
            get
            {
                EnsureLoaded();
                return _ndmfLocalizer;
            }
        }

        public static IReadOnlyCollection<string> AvailableLanguages
        {
            get
            {
                EnsureLoaded();
                return _tables.Keys;
            }
        }

        public static void EnsureLoaded()
        {
            if (_loaded && _tables != null) return;
            _tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            var folder = I18nFolder;
            // Also resolve via this script path so the package works before it is named in Packages/.
            // 同时用脚本路径解析，以便尚未放进 Packages 时也能加载。
            if (!Directory.Exists(folder))
            {
                var script = AssetDatabase.FindAssets("t:Script AtoLoc");
                if (script != null && script.Length > 0)
                {
                    var path = AssetDatabase.GUIDToAssetPath(script[0]);
                    var editor = Path.GetDirectoryName(Path.GetDirectoryName(path));
                    folder = Path.Combine(editor ?? "", "i18n").Replace('\\', '/');
                }
            }

            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var table = JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                                    ?? new Dictionary<string, string>();
                        var name = Path.GetFileNameWithoutExtension(file);
                        _tables[Normalize(name)] = table;
                        LanguagePrefs.RegisterLanguage(Normalize(name));
                        AtoLog.VerboseLog($"Loaded i18n {name} ({table.Count} keys) from {file}");
                    }
                    catch (Exception e)
                    {
                        AtoLog.Warn($"Failed to load i18n {file}: {e.Message}");
                    }
                }
            }

            _ndmfLocalizer = new Localizer("en-us", () =>
            {
                var list = new List<(string, Func<string, string>)>();
                foreach (var kv in _tables)
                {
                    var table = kv.Value;
                    list.Add((kv.Key, key => table.TryGetValue(key, out var s) ? s : null));
                }
                return list;
            });

            _loaded = true;
        }

        public static void Reload()
        {
            _loaded = false;
            _tables = null;
            EnsureLoaded();
        }

        public static string Normalize(string lang)
        {
            if (string.IsNullOrEmpty(lang)) return "en-us";
            lang = lang.ToLowerInvariant().Replace('_', '-');
            if (lang == "zh-cn" || lang == "zh" || lang == "zh-hans-cn") return "zh-hans";
            if (lang == "en" || lang == "en-gb") return "en-us";
            return lang;
        }

        public static string ResolveLanguage(AtoLanguageMode mode)
        {
            EnsureLoaded();
            switch (mode)
            {
                case AtoLanguageMode.English: return "en-us";
                case AtoLanguageMode.SimplifiedChinese: return "zh-hans";
                default:
                    return Normalize(LanguagePrefs.Language);
            }
        }

        public static string T(string key, AtoLanguageMode mode = AtoLanguageMode.Auto)
        {
            EnsureLoaded();
            var lang = ResolveLanguage(mode);
            if (_tables.TryGetValue(lang, out var table) && table.TryGetValue(key, out var s) && !string.IsNullOrEmpty(s))
                return s;
            // Fallback to English. / 回退英文。
            if (_tables.TryGetValue("en-us", out var en) && en.TryGetValue(key, out var enS) && !string.IsNullOrEmpty(enS))
                return enS;
            return key;
        }

        public static string T(AtoLanguageMode mode, string key, params object[] args)
        {
            var fmt = T(key, mode);
            try { return args != null && args.Length > 0 ? string.Format(CultureInfo.InvariantCulture, fmt, args) : fmt; }
            catch { return fmt; }
        }

        /// <summary>
        /// Minimal parser for a flat JSON string dictionary. / 扁平 JSON 字符串字典的最小解析器。
        /// </summary>
        private static Dictionary<string, string> ParseFlatJson(string json)
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
                var key = ReadString();
                SkipWs();
                if (i < json.Length && json[i] == ':') i++;
                SkipWs();
                var val = ReadString();
                if (key != null) table[key] = val ?? "";
                SkipWs();
            }
            return table;

            void SkipWs()
            {
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            }

            string ReadString()
            {
                SkipWs();
                if (i >= json.Length || json[i] != '"') return null;
                i++;
                var sb = new StringBuilder();
                while (i < json.Length)
                {
                    var c = json[i++];
                    if (c == '"') break;
                    if (c == '\\' && i < json.Length)
                    {
                        var n = json[i++];
                        switch (n)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case 'u':
                                if (i + 4 <= json.Length)
                                {
                                    var hex = json.Substring(i, 4);
                                    if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                                        sb.Append((char)cp);
                                    i += 4;
                                }
                                break;
                            default: sb.Append(n); break;
                        }
                    }
                    else sb.Append(c);
                }
                return sb.ToString();
            }
        }
    }
}
