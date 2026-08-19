using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// JSON-driven i18n. Discovers every *.json next to this script; Auto follows NDMF LanguagePrefs.
    /// JSON 驱动的 i18n。扫描同目录全部 json；Auto 跟随 NDMF 当前语言，缺翻译回退英文。
    /// </summary>
    public static class ATOLoc
    {
        public const string DefaultLang = "en-US";
        private static Localizer _ndmfLocalizer;
        private static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;
        private static ATOLanguageMode _mode = ATOLanguageMode.Auto;

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
                return Tables.Keys;
            }
        }

        public static void SetMode(ATOLanguageMode mode)
        {
            _mode = mode;
        }

        public static string CurrentLang
        {
            get
            {
                if (_mode == ATOLanguageMode.English) return "en-US";
                if (_mode == ATOLanguageMode.SimplifiedChinese) return "zh-Hans";
                var ndmf = LanguagePrefs.Language ?? "en-us";
                return Normalize(ndmf);
            }
        }

        public static string T(string key)
        {
            EnsureLoaded();
            if (TryGet(CurrentLang, key, out var s)) return s;
            if (TryGet(DefaultLang, key, out s)) return s;
            return key;
        }

        public static string T(string key, params object[] args)
        {
            var raw = T(key);
            try { return string.Format(raw, args); }
            catch (FormatException) { return raw; }
        }

        public static void Report(ErrorSeverity severity, string key, params object[] args)
        {
            ErrorReport.ReportError(NdmfLocalizer, severity, key, args);
        }

        private static bool TryGet(string lang, string key, out string value)
        {
            value = null;
            if (!Tables.TryGetValue(lang, out var table)) return false;
            return table.TryGetValue(key, out value) && !string.IsNullOrEmpty(value);
        }

        private static string Normalize(string lang)
        {
            if (string.IsNullOrEmpty(lang)) return DefaultLang;
            lang = lang.Replace('_', '-');
            if (lang.StartsWith("zh-hans", StringComparison.OrdinalIgnoreCase) ||
                lang.Equals("zh-cn", StringComparison.OrdinalIgnoreCase) ||
                lang.Equals("zh", StringComparison.OrdinalIgnoreCase))
                return "zh-Hans";
            if (lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en-US";

            foreach (var key in Tables.Keys)
            {
                if (key.Equals(lang, StringComparison.OrdinalIgnoreCase)) return key;
                if (lang.StartsWith(key.Split('-')[0], StringComparison.OrdinalIgnoreCase)) return key;
            }
            return lang;
        }

        internal static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            var folder = LocateLocalizationFolder();
            if (folder != null && Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder, "*.json"))
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        var json = File.ReadAllText(file);
                        Tables[name] = ParseFlatJson(json);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"{AvatarTextureOptimizer.LogPrefix} Failed to load i18n {file}: {e.Message}");
                    }
                }
            }

            _ndmfLocalizer = new Localizer("en-us", () =>
            {
                var list = new List<(string, Func<string, string>)>();
                foreach (var kv in Tables)
                {
                    var table = kv.Value;
                    var iso = kv.Key.ToLowerInvariant();
                    list.Add((iso, k => table.TryGetValue(k, out var v) ? v : null));
                }
                return list;
            });
        }

        private static string LocateLocalizationFolder()
        {
            var guids = AssetDatabase.FindAssets("en-US t:TextAsset");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Replace('\\', '/').Contains("net.fosa.avatar-texture-optimizer") &&
                    path.EndsWith("Editor/Localization/en-US.json", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(Path.GetFullPath(path));
                }
            }

            // Fallback: walk up from this script. / 回退：从本脚本向上找。
            var script = FindScriptPath();
            if (script != null)
            {
                return Path.GetDirectoryName(script);
            }
            return Path.Combine("Packages", AvatarTextureOptimizer.PackageName, "Editor", "Localization");
        }

        private static string FindScriptPath()
        {
            var guids = AssetDatabase.FindAssets("ATOLoc t:MonoScript");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("ATOLoc.cs")) return Path.GetFullPath(path);
            }
            return null;
        }

        /// <summary>
        /// Tiny flat-JSON parser (`"key": "value"`). Avoids a Newtonsoft hard dependency.
        /// 极简扁平 JSON 解析。避免强制依赖 Newtonsoft。
        /// </summary>
        internal static Dictionary<string, string> ParseFlatJson(string json)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(json)) return result;
            int i = 0;
            int n = json.Length;
            while (i < n)
            {
                if (json[i] != '"') { i++; continue; }
                if (!TryReadString(json, ref i, out var key)) break;
                while (i < n && char.IsWhiteSpace(json[i])) i++;
                if (i >= n || json[i] != ':') continue;
                i++;
                while (i < n && char.IsWhiteSpace(json[i])) i++;
                if (i < n && json[i] == '"')
                {
                    if (TryReadString(json, ref i, out var value))
                        result[key] = value;
                }
            }
            return result;
        }

        private static bool TryReadString(string json, ref int i, out string value)
        {
            value = null;
            if (i >= json.Length || json[i] != '"') return false;
            i++;
            var sb = new System.Text.StringBuilder();
            while (i < json.Length)
            {
                var c = json[i++];
                if (c == '\\')
                {
                    if (i >= json.Length) return false;
                    var e = json[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'u':
                            if (i + 4 <= json.Length &&
                                int.TryParse(json.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                            {
                                sb.Append((char)cp);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else if (c == '"')
                {
                    value = sb.ToString();
                    return true;
                }
                else sb.Append(c);
            }
            return false;
        }
    }
}
