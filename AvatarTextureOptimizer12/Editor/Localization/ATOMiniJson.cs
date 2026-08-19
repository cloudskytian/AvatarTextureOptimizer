// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Minimal JSON reader used by the i18n loader.
// AvatarTextureOptimizer (ATO) - i18n 加载器使用的最小 JSON 读取器。
//
// EN: We deliberately avoid a hard dependency on Newtonsoft.Json (not guaranteed to be present) and
//     Unity's JsonUtility (cannot deserialize dictionaries). The format we accept is a flat JSON object
//     whose values are strings; nested objects are flattened with ':' separators so that translators can
//     group keys naturally.
// ZH: 我们刻意不强依赖 Newtonsoft.Json（不保证存在），也不用 Unity 的 JsonUtility（无法反序列化字典）。
//     支持的格式为“值均为字符串的扁平 JSON 对象”；嵌套对象会用 ':' 连接扁平化，方便译者按组书写。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Localization
{
    internal static class ATOMiniJson
    {
        /// <summary>
        /// EN: Parse a flat/nested JSON object into a flattened string dictionary. Returns null on error.
        /// ZH: 将扁平或嵌套的 JSON 对象解析为扁平化的字符串字典。出错返回 null。
        /// </summary>
        public static Dictionary<string, string> ParseFlat(string text, out string error)
        {
            error = null;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                int i = 0;
                SkipWs(text, ref i);
                if (i >= text.Length || text[i] != '{') throw new FormatException("expected '{' at root");
                ParseObject(text, ref i, "", result);
                SkipWs(text, ref i);
                return result;
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        private static void ParseObject(string s, ref int i, string prefix, IDictionary<string, string> into)
        {
            Expect(s, ref i, '{');
            SkipWs(s, ref i);
            if (Peek(s, i) == '}') { i++; return; }

            while (true)
            {
                SkipWs(s, ref i);
                var key = ParseString(s, ref i);
                SkipWs(s, ref i);
                Expect(s, ref i, ':');
                SkipWs(s, ref i);

                var full = prefix.Length == 0 ? key : prefix + ":" + key;
                var c = Peek(s, i);
                if (c == '{')
                {
                    ParseObject(s, ref i, full, into);
                }
                else if (c == '"')
                {
                    into[full] = ParseString(s, ref i);
                }
                else
                {
                    // EN: Numbers / booleans / null are coerced to their literal text.
                    // ZH: 数字 / 布尔 / null 直接按字面文本处理。
                    into[full] = ParseLiteral(s, ref i);
                }

                SkipWs(s, ref i);
                c = Peek(s, i);
                if (c == ',') { i++; continue; }
                if (c == '}') { i++; return; }
                throw new FormatException($"unexpected '{c}' at {i}");
            }
        }

        private static string ParseLiteral(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && ",}\r\n \t".IndexOf(s[i]) < 0) i++;
            return s.Substring(start, i - start);
        }

        private static string ParseString(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                var c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) throw new FormatException("unterminated escape");
                var e = s[i++];
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
                        if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)ushort.Parse(s.Substring(i, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException($"bad escape '\\{e}'");
                }
            }
        }

        private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException($"expected '{c}' at {i}");
            i++;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                var c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\uFEFF') { i++; continue; }
                // EN: tolerate // line comments, which translators often add.
                // ZH: 容忍译者常写的 // 行注释。
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }
                break;
            }
        }
    }
}
