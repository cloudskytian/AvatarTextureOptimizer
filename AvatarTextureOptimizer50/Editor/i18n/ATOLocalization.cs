// -----------------------------------------------------------------------------
// ATOLocalization.cs — JSON-file based, user-extensible i18n.
// ATOLocalization.cs — 基于 JSON 文件、可由用户扩展的 i18n。
//
// Any *.json dropped into the package's Localization folder (or a user folder
// registered via ATOApi) appears as a language automatically. "auto" follows the
// NDMF language; missing keys fall back to English (then to the raw key).
// 放入包内 Localization（或通过 ATOApi 注册的用户目录）的任意 *.json 都会自动成为可选语言。
// auto 跟随 NDMF 语言；缺失键回退英文（再退回键名本身）。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class ATOLocalization
    {
        /// <summary>Extra folders registered by users/third parties / 用户或第三方注册的附加目录。</summary>
        private static readonly List<string> _extraFolders = new List<string>();

        /// <summary>Language override ("auto" = follow NDMF). / 语言覆盖（auto=跟随 NDMF）。</summary>
        internal static string LanguageOverride = "auto";

        /// <summary>Currently selected UI language code / 当前界面语言代码。</summary>
        internal static string CurrentLanguage
        {
            get
            {
                if (LanguageOverride != "auto" && !string.IsNullOrEmpty(LanguageOverride)) return LanguageOverride;
                return LanguagePrefs.Language;
            }
        }

        /// <summary>Register an additional localization folder (call from static ctors).
        /// 注册附加本地化目录（可在静态构造中调用）。</summary>
        public static void RegisterFolder(string absoluteFolder)
        {
            if (Directory.Exists(absoluteFolder) && !_extraFolders.Contains(absoluteFolder))
                _extraFolders.Add(absoluteFolder);
        }

        /// <summary>All discovered languages (folder file names without extension, sorted).
        /// 发现的全部语言（文件名去扩展名，排序返回）。</summary>
        public static List<string> AvailableLanguages()
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in EnumerateFolders())
            {
                foreach (var file in Directory.GetFiles(f, "*.json"))
                {
                    var lang = Path.GetFileNameWithoutExtension(file);
                    if (!string.IsNullOrEmpty(lang)) set.Add(lang);
                }
            }

            return set.ToList();
        }

        private static IEnumerable<string> EnumerateFolders()
        {
            // Package Localization folder via the ndmf package-resolve convention:
            // ndmf resolves "Packages/<pkg>/Localization/<locale>.json" for its own Localizer;
            // we resolve ours relative to this assembly's location (robust for both Packages/ and Assets/ installs).
            // ndmf 按其约定解析 Localization；这里按本程序集位置解析（Packages/ 与 Assets/ 安装均适用）。
            var pkgRoot = LocatePackageRoot();
            if (pkgRoot != null)
            {
                var loc = Path.Combine(pkgRoot, "Localization");
                if (Directory.Exists(loc)) yield return loc;
            }

            foreach (var f in _extraFolders) yield return f;
        }

        private static string LocatePackageRoot()
        {
            try
            {
                var script = new System.Diagnostics.StackTrace(1, false);
                // Deterministic approach: find the asset path of this script's assembly info is fragile;
                // use AssetDatabase to locate any asset under our package name instead.
                foreach (var guid in AssetDatabase.FindAssets("ATOComponent t:Script"))
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    // ".../net.fosa.avatar-texture-optimizer/Runtime/ATOComponent.cs"
                    var i = p?.IndexOf("net.fosa.avatar-texture-optimizer", StringComparison.OrdinalIgnoreCase) ?? -1;
                    if (i >= 0) return p.Substring(0, i + "net.fosa.avatar-texture-optimizer".Length);
                }
            }
            catch (Exception) { }

            return null;
        }

        private static Dictionary<string, string> _cache;   // lang -> merged dict of THAT lang
        private static string _cacheLang;
        private static string _cacheStamp;

        private static string FoldersStamp() => string.Join("|", EnumerateFolders());

        private static Dictionary<string, string> LoadLang(string lang)
        {
            var merged = new Dictionary<string, string>();
            foreach (var folder in EnumerateFolders())
            {
                var file = Path.Combine(folder, lang + ".json");
                if (!File.Exists(file)) continue;
                try
                {
                    var json = File.ReadAllText(file);
                    foreach (var kv in ParseFlatJson(json))
                        merged[kv.Key] = kv.Value; // later folders win / 后注册目录优先
                }
                catch (Exception e)
                {
                    ATOLog.Warn($"Failed to parse i18n file {file}: {e.Message}");
                }
            }

            return merged;
        }

        /// <summary>Minimal flat JSON parser for { "key": "value" } string maps (no nested objects).
        /// 仅支持 { "key": "value" } 扁平字符串表的最小 JSON 解析器。</summary>
        internal static IEnumerable<KeyValuePair<string, string>> ParseFlatJson(string json)
        {
            var s = json;
            int i = 0;
            Expect('{');
            SkipWs();
            if (i < s.Length && s[i] == '}') yield break;

            while (true)
            {
                SkipWs();
                Expect('"');
                var key = ReadString();
                SkipWs();
                Expect(':');
                SkipWs();
                Expect('"');
                var value = ReadString();
                SkipWs();
                yield return new KeyValuePair<string, string>(key, value);
                if (i < s.Length && s[i] == ',') { i++; continue; }

                break;
            }

            void SkipWs() { while (i < s.Length && (s[i] == ' ' || s[i] == '\n' || s[i] == '\r' || s[i] == '\t')) i++; }
            void Expect(char c)
            {
                SkipWs();
                if (i >= s.Length || s[i] != c) throw new FormatException($"Expected '{c}' at {i}");
                i++;
            }
            string ReadString()
            {
                var sb = new System.Text.StringBuilder();
                while (i < s.Length && s[i] != '"')
                {
                    char c = s[i++];
                    if (c == '\\' && i < s.Length)
                    {
                        char e = s[i++];
                        switch (e)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            case 'r': sb.Append('\r'); break;
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'u':
                                if (i + 4 <= s.Length &&
                                    ushort.TryParse(s.Substring(i, 4),
                                        System.Globalization.NumberStyles.HexNumber, null, out var code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                                else sb.Append('u');
                                break;
                            default: sb.Append(e); break;
                        }
                    }
                    else sb.Append(c);
                }

                if (i >= s.Length) throw new FormatException("Unterminated string");
                i++; // closing quote / 结束引号
                return sb.ToString();
            }
        }

        /// <summary>Look up a localized string with fallback en-US → key.
        /// 查询本地化字符串，回退 en-US，再退回键名。</summary>
        public static string L(string key)
        {
            var lang = CurrentLanguage;
            var stamp = FoldersStamp();
            if (_cache == null || _cacheLang != lang || _cacheStamp != stamp)
            {
                _cache = LoadLang(lang);
                _cacheLang = lang;
                _cacheStamp = stamp;
            }

            if (_cache.TryGetValue(key, out var v)) return v;

            if (!lang.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            {
                var en = LoadLang("en-US");
                if (en.TryGetValue(key, out var ev)) return ev;
            }

            return key;
        }

        public static string F(string key, params object[] args)
        {
            try { return string.Format(L(key), args); }
            catch (Exception) { return L(key); }
        }

        // ------------------------------------------------------------------ //
        //  NDMF Localizer bridge — used for the NDMF error console report.
        //  NDMF Localizer 桥接——用于 NDMF 错误控制台报告。
        // ------------------------------------------------------------------ //

        private static Localizer _ndmfLocalizer;

        /// <summary>NDMF Localizer fed by our JSON files (all languages registered).
        /// 由我们的 JSON 文件驱动的 NDMF Localizer（注册全部语言）。</summary>
        public static Localizer NdmfLocalizer
        {
            get
            {
                if (_ndmfLocalizer == null)
                {
                    _ndmfLocalizer = new Localizer("en-US", () =>
                    {
                        var list = new List<(string, Func<string, string>)>();
                        foreach (var lang in AvailableLanguages())
                        {
                            var dict = LoadLang(lang);
                            list.Add((lang, k => dict.TryGetValue(k, out var v) ? v : null));
                        }

                        return list;
                    });
                }

                return _ndmfLocalizer;
            }
        }
    }
}
