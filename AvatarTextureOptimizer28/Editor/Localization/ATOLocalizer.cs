using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: Loads user-extensible JSON localisation files and exposes them through NDMF's
    ///     <see cref="Localizer"/>. Any *.json placed in the package's Localization folder - or in
    ///     Assets/ATO_Localization - is picked up automatically, so third parties can ship a new
    ///     language without touching our code. The file name (minus extension) is the BCP-47 code.
    ///     Missing keys fall back to English.
    /// ZH: 加载可由用户扩展的 JSON 本地化文件，并通过 NDMF 的 <see cref="Localizer"/> 暴露出来。
    ///     放在包的 Localization 文件夹（或 Assets/ATO_Localization）下的任意 *.json 都会被自动识别，
    ///     因此第三方无需改动我们的代码即可新增语言。文件名（去掉扩展名）即为 BCP-47 语言代码。
    ///     缺失的键回退到英文。
    /// </summary>
    public static class ATOLocalizer
    {
        private const string PackageLocalizationPath =
            "Packages/" + ATOConstants.PackageName + "/Localization";

        private const string UserLocalizationPath = "Assets/ATO_Localization";

        private static Localizer _localizer;
        private static List<string> _available;

        /// <summary>EN: The shared NDMF localizer instance. ZH: 共享的 NDMF 本地化器实例。</summary>
        public static Localizer L => _localizer ??= Build();

        /// <summary>
        /// EN: Language codes we actually found files for, sorted. Used to populate the UI dropdown,
        ///     so "however many config files exist, that many languages are shown".
        /// ZH: 实际找到配置文件的语言代码（已排序）。用于填充 UI 下拉，
        ///     从而实现"有几个语言配置文件就显示几个语言"。
        /// </summary>
        public static IReadOnlyList<string> AvailableLanguages
        {
            get
            {
                if (_available == null) _ = L;
                return _available;
            }
        }

        private static Localizer Build()
        {
            return new Localizer("en", LoadAll);
        }

        private static List<(string, Func<string, string>)> LoadAll()
        {
            var byLang = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            // EN: User overrides are loaded last so they win over the bundled files.
            // ZH: 用户覆盖文件最后加载，以便优先于内置文件。
            foreach (var dir in new[] { PackageLocalizationPath, UserLocalizationPath })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
                {
                    var lang = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrEmpty(lang)) continue;
                    try
                    {
                        var dict = ParseFlatJson(File.ReadAllText(file));
                        if (!byLang.TryGetValue(lang, out var existing))
                            byLang[lang] = dict;
                        else
                            foreach (var kv in dict) existing[kv.Key] = kv.Value;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning(
                            $"{ATOConstants.LogPrefix} Failed to parse localization file '{file}': {e.Message}");
                    }
                }
            }

            if (byLang.Count == 0)
            {
                // EN: Never leave the localizer empty; an empty dictionary makes every key render raw.
                // ZH: 绝不让本地化器为空；空字典会让所有键原样显示。
                byLang["en"] = new Dictionary<string, string>();
            }

            _available = byLang.Keys.OrderBy(NormalizeSafe, StringComparer.OrdinalIgnoreCase).ToList();

            return byLang.Select(kv =>
            {
                var table = kv.Value;
                return (kv.Key, (Func<string, string>)(key => table.TryGetValue(key, out var v) ? v : null));
            }).ToList();
        }

        private static string NormalizeSafe(string lang)
        {
            try { return CultureInfo.GetCultureInfo(lang).Name; }
            catch { return lang; }
        }

        /// <summary>
        /// EN: Apply the component's language preference. Auto leaves NDMF in charge; Manual forces a code.
        /// ZH: 应用组件的语言偏好。Auto 交由 NDMF 决定；Manual 强制指定代码。
        /// </summary>
        public static void ApplyPreference(ATOLanguageMode mode, string manual)
        {
            if (mode == ATOLanguageMode.Manual && !string.IsNullOrEmpty(manual))
            {
                if (!string.Equals(LanguagePrefs.Language, manual, StringComparison.OrdinalIgnoreCase))
                    LanguagePrefs.Language = manual;
            }
        }

        /// <summary>EN: Shorthand lookup. ZH: 查表简写。</summary>
        public static string Tr(string key) => L.GetLocalizedString(key);

        /// <summary>EN: Lookup with string.Format arguments. ZH: 带 string.Format 参数的查表。</summary>
        public static string Tr(string key, params object[] args)
        {
            var s = L.GetLocalizedString(key);
            try { return string.Format(s, args); }
            catch (FormatException) { return s; }
        }

        /// <summary>EN: Force a reload, e.g. after the user edited a json file. ZH: 强制重新加载，例如用户改了 json 之后。</summary>
        [MenuItem("Tools/Avatar Texture Optimizer/Reload Localizations")]
        public static void Reload()
        {
            _localizer = null;
            _available = null;
            Localizer.ReloadLocalizations();
        }

        // -----------------------------------------------------------------------------------------
        // EN: A tiny flat JSON object parser. We avoid JsonUtility because it cannot deserialise an
        //     arbitrary string->string map, and we avoid Newtonsoft because it is not guaranteed to be
        //     present in every project. Only a flat object of string values is supported, which is
        //     exactly the documented format for ATO localisation files.
        // ZH: 一个极小的扁平 JSON 对象解析器。不用 JsonUtility 是因为它无法反序列化任意的
        //     string->string 映射；不用 Newtonsoft 是因为它不保证在每个工程中都存在。
        //     仅支持值为字符串的扁平对象，这正是 ATO 本地化文件的既定格式。
        // -----------------------------------------------------------------------------------------
        private static Dictionary<string, string> ParseFlatJson(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = 0;
            SkipWs(text, ref i);
            Expect(text, ref i, '{');
            SkipWs(text, ref i);
            if (Peek(text, i) == '}') return result;

            while (i < text.Length)
            {
                SkipWs(text, ref i);
                var key = ReadString(text, ref i);
                SkipWs(text, ref i);
                Expect(text, ref i, ':');
                SkipWs(text, ref i);
                var value = ReadString(text, ref i);
                result[key] = value;
                SkipWs(text, ref i);
                var c = Peek(text, i);
                if (c == ',') { i++; continue; }
                if (c == '}') { i++; break; }
                throw new FormatException($"Unexpected character '{c}' at offset {i}");
            }
            return result;
        }

        private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException($"Expected '{c}' at offset {i}");
            i++;
        }

        private static string ReadString(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new System.Text.StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                var e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '/': sb.Append('/'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case 'u':
                        if (i + 4 <= s.Length &&
                            ushort.TryParse(s.Substring(i, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out var code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("Unterminated string literal");
        }
    }
}
