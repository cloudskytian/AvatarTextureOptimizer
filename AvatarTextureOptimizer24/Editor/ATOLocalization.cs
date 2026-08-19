// ============================================================================
// ATOLocalization.cs — i18n 系统 / i18n system
// (EN) Loads JSON i18n config files, supports user-extensible languages.
//      Auto mode follows NDMF's current language; falls back to English.
// (ZH) 读取 json 格式 i18n 配置，支持用户扩展语言。Auto 跟随 NDMF 当前语言，
//      缺翻译回退英文。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public static class ATOLocalization
    {
        private const string PackageDir = "Packages/net.fosa.avatar-texture-optimizer/Editor/Localization";
        private const string UserDir = "Assets/ATO/Localization";

        private static Dictionary<string, Dictionary<string, string>> _tables; // lang -> (key -> value)
        private static bool _loaded = false;

        public static IReadOnlyList<string> AvailableLanguages => EnsureLoaded().Keys.OrderBy(x => x).ToList();

        private static Dictionary<string, Dictionary<string, string>> EnsureLoaded()
        {
            if (_loaded && _tables != null) return _tables;
            _tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, lang) in DiscoverFiles())
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var table = ParseFlatObject(json);
                    if (!_tables.TryGetValue(lang, out var existing))
                    {
                        existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        _tables[lang] = existing;
                    }
                    foreach (var kv in table) existing[kv.Key] = kv.Value;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ATO] Failed to load i18n file {path}: {e.Message}");
                }
            }
            _loaded = true;
            return _tables;
        }

        private static IEnumerable<(string path, string lang)> DiscoverFiles()
        {
            var result = new List<(string, string)>();
            foreach (var dir in new[] { PackageDir, UserDir })
            {
                var abs = Path.GetFullPath(dir);
                if (!Directory.Exists(abs)) continue;
                foreach (var f in Directory.GetFiles(abs, "*.json"))
                {
                    var lang = Path.GetFileNameWithoutExtension(f); // e.g. "en", "zh-CN"
                    result.Add((f, lang));
                }
            }
            return result;
        }

        /// <summary>(EN) Translate a key for a language; falls back to English then the key. (ZH) 翻译某个 key；回退英文，最后回退 key。</summary>
        public static string Get(string language, string key)
        {
            var tables = EnsureLoaded();
            if (tables.TryGetValue(language, out var t) && t.TryGetValue(key, out var v)) return v;
            if (tables.TryGetValue("en", out var en) && en.TryGetValue(key, out var v2)) return v2;
            if (tables.TryGetValue("en-US", out var en2) && en2.TryGetValue(key, out var v3)) return v3;
            return key;
        }

        /// <summary>(EN) Exact lookup for a language; returns null if missing (no fallback). Used by the NDMF bridge so its own fallback works. (ZH) 某语言的精确查找；缺失返回 null（不回退）。供 NDMF 桥接使用，使其自身回退生效。</summary>
        public static string GetExact(string language, string key)
        {
            var tables = EnsureLoaded();
            if (tables.TryGetValue(language, out var t) && t.TryGetValue(key, out var v)) return v;
            return null;
        }

        /// <summary>(EN) Resolve the effective language from the component setting. (ZH) 由组件设置解析生效语言。</summary>
        public static string ResolveLanguage(AvatarTextureOptimizer component)
        {
            if (component != null && component.language == ATOLanguage.English) return "en";
            if (component != null && component.language == ATOLanguage.SimplifiedChinese) return "zh-CN";
            // Auto: follow NDMF language
            var ndmfLang = nadena.dev.ndmf.localization.LanguagePrefs.Language ?? "";
            if (ndmfLang.ToLowerInvariant().StartsWith("zh")) return "zh-CN";
            return "en";
        }

        /// <summary>(EN) Translate a key using the given effective language. (ZH) 用生效语言翻译 key。</summary>
        public static string T(string language, string key) => Get(language, key);

        // ---------------- Minimal flat-JSON parser (dependency-free) ----------
        // 极简平铺 JSON 解析器（无外部依赖）。支持 {"key":"value",...} 与字符串转义。

        private static Dictionary<string, string> ParseFlatObject(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            SkipWs(json, ref i);
            if (i >= json.Length || json[i] != '{') throw new FormatException("Expected '{'");
            i++;
            while (true)
            {
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == '}') break;
                var key = ParseString(json, ref i);
                SkipWs(json, ref i);
                if (i >= json.Length || json[i] != ':') throw new FormatException("Expected ':'");
                i++;
                SkipWs(json, ref i);
                var value = ParseString(json, ref i);
                result[key] = value;
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == ',') { i++; continue; }
                break;
            }
            return result;
        }

        private static string ParseString(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '"') throw new FormatException("Expected '\"'");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '"') { i++; return sb.ToString(); }
                if (c == '\\')
                {
                    i++;
                    if (i >= s.Length) break;
                    var e = s[i];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'u':
                            if (i + 4 < s.Length)
                            {
                                sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16));
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
    }
}
