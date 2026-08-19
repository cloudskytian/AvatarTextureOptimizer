// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// I18n.cs — 可扩展 JSON 本地化 / Extensible JSON-based localization
//
// 需求:
//  - 通过读取 json 格式 i18n 配置文件进行本地化显示（有几个语言的配置文件就显示几个语言）。
//  - 提供选项供用户手动切换；默认 Auto 读取 ndmf 的当前语言配置。
//  - 若不存在对应翻译则回退到英文。
//  - 第三方开发者可放入自己的 JSON 文件扩展语言。
//
// 共识: JSON 文件位于本包 i18n/ 目录，命名 <locale>.json（如 en.json / zh-CN.json）；
//       运行时也可加载用户包内的 <package-root>/i18n/*.json（通过 AssetDatabase 扫描）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// ATO i18n 服务 / ATO localization service.
    /// </summary>
    public static class I18n
    {
        private sealed class Locale
        {
            public string code;                       // e.g. "en", "zh-CN"
            public Dictionary<string, string> table;
        }

        private static List<Locale> _locales;
        private static string _curLocale = "";        // current active locale code
        private static string _userChoice = "";       // "auto" / locale code

        /// <summary>所有可用语言代码 / All available locale codes</summary>
        public static IReadOnlyList<string> AvailableLocales => _locales.Select(l => l.code).ToList();

        /// <summary>
        /// 重新加载全部 JSON i18n 文件（包内 i18n/ 目录）。
        /// Reload all JSON localization files from this package's i18n/ directory.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void Reload()
        {
            _locales = new List<Locale>();
            var dir = PackageRoot() + "/i18n";
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
                {
                    try
                    {
                        var code = Path.GetFileNameWithoutExtension(file);
                        var table = ParseFlatJson(File.ReadAllText(file));
                        if (table == null) continue;
                        _locales.Add(new Locale { code = code, table = table });
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[ATO] Failed to load i18n file '{file}': {e.Message}");
                    }
                }
            }

            if (!_locales.Any(l => l.code.Equals("en", StringComparison.OrdinalIgnoreCase)))
            {
                _locales.Insert(0, new Locale { code = "en", table = new Dictionary<string, string>() });
            }

            _curLocale = ResolveLocale();
        }

        /// <summary>包根目录（Editor 用） / Package root path (editor)</summary>
        public static string PackageRoot()
        {
            // 本文件位于 <root>/Editor/I18n.cs → 根目录为上两级 / This file lives at <root>/Editor/I18n.cs
            var me = AssetDatabase.FindAssets("I18n.cs t:MonoScript")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.EndsWith("/Editor/I18n.cs", StringComparison.Ordinal));
            if (string.IsNullOrEmpty(me)) return "Packages/net.fosa.avatar-texture-optimizer";
            return me.Substring(0, me.Length - "/Editor/I18n.cs".Length);
        }

        /// <summary>
        /// 轻量 JSON 解析：仅支持扁平字符串对象 {"key": "value", ...}（本工具 i18n 结构）。
        /// Lightweight JSON parser: flat string-object only (this tool's i18n structure).
        /// </summary>
        private static Dictionary<string, string> ParseFlatJson(string text)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            int n = text.Length;
            while (i < n)
            {
                // 跳过空白与非 '"' 字符（找下一个字符串键）
                while (i < n && text[i] != '"') i++;
                if (i >= n) break;
                i++; // 跳过开引号
                string key = ReadString(text, ref i);
                if (key == null) break;
                // 找 ':' 
                while (i < n && text[i] != ':') i++;
                if (i >= n) break;
                i++;
                // 找字符串值
                while (i < n && text[i] != '"') i++;
                if (i >= n) break;
                i++;
                string value = ReadString(text, ref i);
                if (value == null) break;
                dict[key] = value;
            }
            return dict;
        }

        private static string ReadString(string text, ref int i)
        {
            var sb = new System.Text.StringBuilder();
            while (i < text.Length)
            {
                char c = text[i++];
                if (c == '\\' && i < text.Length)
                {
                    char e = text[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'u':
                            if (i + 4 <= text.Length &&
                                int.TryParse(text.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else if (c == '"')
                {
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                }
            }
            return null;
        }

        /// <summary>用户语言选择（'auto' 或语言代码） / User language choice</summary>
        public static string UserChoice
        {
            get => _userChoice;
            set { _userChoice = value; _curLocale = ResolveLocale(); }
        }

        private static string ResolveLocale()
        {
            string want;
            if (_userChoice == "auto")
            {
                want = LanguagePrefs.Language; // e.g. "zh-hans" / "en-us"
                want = want.Replace("-", "");
            }
            else
            {
                want = _userChoice.Replace("-", "");
            }

            // 精确匹配（忽略大小写与分隔符）→ 前缀匹配 → 英文回退
            var normalized = want.ToLowerInvariant();
            foreach (var l in _locales)
            {
                if (l.code.Replace("-", "").ToLowerInvariant() == normalized) return l.code;
            }
            foreach (var l in _locales)
            {
                if (l.code.Replace("-", "").ToLowerInvariant().StartsWith(normalized, StringComparison.Ordinal)) return l.code;
            }
            foreach (var l in _locales)
            {
                if (normalized.StartsWith(l.code.Replace("-", "").ToLowerInvariant(), StringComparison.Ordinal)) return l.code;
            }
            return "en";
        }

        /// <summary>当前语言代码 / Current active locale code</summary>
        public static string CurrentLocale => _curLocale;

        /// <summary>
        /// 翻译字符串 / Translate a key.
        /// </summary>
        public static string T(string key)
        {
            return T(key, Array.Empty<object>());
        }

        /// <summary>
        /// 翻译并格式化（{0}/{1}...） / Translate and format.
        /// </summary>
        public static string T(string key, params object[] args)
        {
            if (_locales == null) return key; // 防御：未加载时直接返回键 / defensive: unloaded → return key
            var table = _locales.FirstOrDefault(l => l.code == _curLocale)?.table;
            if (table != null && table.TryGetValue(key, out var v)) return SafeFormat(v, args);
            var en = _locales.FirstOrDefault(l => l.code == "en")?.table;
            if (en != null && en.TryGetValue(key, out var v2)) return SafeFormat(v2, args);
            return key;
        }

        private static string SafeFormat(string s, object[] args)
        {
            if (args == null || args.Length == 0) return s;
            try { return string.Format(CultureInfo.InvariantCulture, s, args); }
            catch (FormatException) { return s; }
        }

        /// <summary>手动触发重载（编辑器里语言改动后调用） / Manual reload</summary>
        public static void Refresh()
        {
            Reload();
        }
    }
}
