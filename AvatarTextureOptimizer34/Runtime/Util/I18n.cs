// AvatarTextureOptimizer - I18n
// EN: Tiny JSON-based i18n. Language files are plain {"key":"value"} JSON. Fallback: English.
// CN: 极简 JSON 本地化。语言文件为 {"key":"value"} JSON，缺失回退英文。
using System;
using System.Collections.Generic;
using System.Globalization;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// EN: Runtime-agnostic i18n table. Editor code loads JSON files; lookups happen here.
    /// CN: 与运行时无关的 i18n 表。编辑器代码加载 JSON，此处负责查表。
    /// </summary>
    public static class I18n
    {
        private sealed class Language
        {
            public readonly Dictionary<string, string> Table = new Dictionary<string, string>();
        }

        private static readonly Dictionary<string, Language> Languages = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> Available = new List<string>();

        /// <summary>EN: Current language code, e.g. "en" or "zh-CN". / CN: 当前语言代码。</summary>
        public static string CurrentLanguage { get; private set; } = "en";

        /// <summary>EN: Manually selected language; null = auto (NDMF editor language). / CN: 手动选择语言；null = 自动。</summary>
        public static string ManualLanguage { get; set; }

        public static IReadOnlyList<string> AvailableLanguages => Available;

        /// <summary>
        /// EN: Registers (or replaces) a language from raw JSON text. Called by editor bootstrap.
        /// CN: 从 JSON 文本注册（或替换）一种语言。由编辑器启动代码调用。
        /// </summary>
        public static void AddLanguage(string code, string jsonText)
        {
            if (string.IsNullOrEmpty(jsonText)) return;
            var lang = new Language();
            try
            {
                var parsed = UnityEngine.JsonUtility.FromJson<Dictionary<string, string>>(jsonText);
                if (parsed == null)
                {
                    // EN: JsonUtility does not deserialize plain dictionaries; fall back to manual parse.
                    // CN: JsonUtility 不支持普通字典反序列化；退化为手工解析。
                    parsed = ParseSimpleJson(jsonText);
                }
                foreach (var kv in parsed) lang.Table[kv.Key] = kv.Value;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[ATO] Failed to load language {code}: {e.Message}");
                return;
            }
            Languages[code] = lang;
            if (!Available.Contains(code)) Available.Add(code);
        }

        public static void SetLanguage(string code)
        {
            if (Languages.ContainsKey(code)) CurrentLanguage = code;
        }

        /// <summary>
        /// EN: Resolves a key in the current language, falling back to English, then to the raw key.
        /// CN: 在当前语言解析键，回退英文，最后回退原始键。
        /// </summary>
        public static string T(string key, params object[] args)
        {
            string value = Lookup(CurrentLanguage, key);
            if (value == null) value = Lookup("en", key);
            if (value == null) value = key;
            if (args == null || args.Length == 0) return value;
            try { return string.Format(CultureInfo.InvariantCulture, value, args); }
            catch (FormatException) { return value; }
        }

        private static string Lookup(string lang, string key)
        {
            if (Languages.TryGetValue(lang, out var l) && l.Table.TryGetValue(key, out var v)) return v;
            return null;
        }

        // EN: Minimal JSON object parser (flat string/string map). Robust to BOM and CRLF.
        // CN: 极简 JSON 对象解析器（扁平 string/string 映射），兼容 BOM 与 CRLF。
        internal static Dictionary<string, string> ParseSimpleJson(string text)
        {
            var result = new Dictionary<string, string>();
            text = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (text.Length < 2 || text[0] != '{') return result;
            int i = 1;
            while (i < text.Length)
            {
                i = SkipWs(text, i);
                if (i >= text.Length || text[i] == '}') break;
                string key = ReadString(text, ref i);
                i = SkipWs(text, i);
                if (i < text.Length && text[i] == ':') i++;
                i = SkipWs(text, i);
                string value = ReadString(text, ref i);
                result[key] = value;
                i = SkipWs(text, i);
                if (i < text.Length && text[i] == ',') i++;
            }
            return result;
        }

        private static int SkipWs(string s, int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
            return i;
        }

        private static string ReadString(string s, ref int i)
        {
            while (i < s.Length && s[i] != '"') i++;
            if (i >= s.Length) return "";
            i++; // skip opening quote
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
                        case 'u':
                            if (i + 4 <= s.Length && int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                            { sb.Append((char)cp); i += 4; }
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
