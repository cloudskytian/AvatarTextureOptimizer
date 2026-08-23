using System;
using System.Collections.Generic;
using System.Text;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Minimal flat JSON parser for i18n files: {"key":"value", ...}. No external dependency so
    /// users can drop new localization json files without any package requirement.
    /// / 极简扁平 JSON 解析器（仅字符串键值对），零依赖；用户放置新语言 json 即可扩展。
    /// </summary>
    internal static class AtoMiniJson
    {
        /// <summary>Parse a flat json object into a string dictionary. Throws FormatException on malformed input. / 解析扁平 json 对象为字典。</summary>
        internal static Dictionary<string, string> Parse(string text)
        {
            var result = new Dictionary<string, string>();
            int i = 0;
            SkipWs(text, ref i);
            Expect(text, ref i, '{');
            SkipWs(text, ref i);
            if (i < text.Length && text[i] == '}') return result;

            while (true)
            {
                SkipWs(text, ref i);
                var key = ParseString(text, ref i);
                SkipWs(text, ref i);
                Expect(text, ref i, ':');
                SkipWs(text, ref i);
                var value = ParseString(text, ref i);
                result[key] = value;
                SkipWs(text, ref i);
                if (i >= text.Length) throw new FormatException("ATO i18n: unexpected end of json / 意外的文件结束");
                if (text[i] == ',') { i++; continue; }
                if (text[i] == '}') { i++; break; }
                throw new FormatException($"ATO i18n: unexpected char '{text[i]}' at {i} / 非法字符");
            }

            return result;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c)
                throw new FormatException($"ATO i18n: expected '{c}' at {i} / 期望字符 '{c}'");
            i++;
        }

        private static string ParseString(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
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
                        if (i + 4 > s.Length) throw new FormatException("ATO i18n: bad \\u escape / 非法 \\u 转义");
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: throw new FormatException($"ATO i18n: bad escape '\\{e}' / 非法转义");
                }
            }

            throw new FormatException("ATO i18n: unterminated string / 字符串未闭合");
        }
    }
}
