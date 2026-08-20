// Minimal dependency-free JSON parser (objects, arrays, strings, numbers, bool, null).
// 零依赖迷你 JSON 解析器（对象/数组/字符串/数字/布尔/null）。
// Used only for i18n config files; not a general-purpose JSON library.
// 仅用于 i18n 配置文件解析，非通用 JSON 库。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace net.fosa.ato.editor
{
    internal static class MiniJson
    {
        internal static object Parse(string text)
        {
            int pos = 0;
            object v = ParseValue(text, ref pos);
            SkipWs(text, ref pos);
            if (pos != text.Length) throw new FormatException($"trailing characters at {pos}");
            return v;
        }

        private static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        private static object ParseValue(string s, ref int pos)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length) throw new FormatException("unexpected end of json");
            char c = s[pos];
            switch (c)
            {
                case '{': return ParseObject(s, ref pos);
                case '[': return ParseArray(s, ref pos);
                case '"': return ParseString(s, ref pos);
                case 't': Expect(s, ref pos, "true"); return true;
                case 'f': Expect(s, ref pos, "false"); return false;
                case 'n': Expect(s, ref pos, "null"); return null;
                default: return ParseNumber(s, ref pos);
            }
        }

        private static void Expect(string s, ref int pos, string word)
        {
            if (string.CompareOrdinal(s, pos, word, 0, word.Length) != 0)
                throw new FormatException($"invalid literal at {pos}");
            pos += word.Length;
        }

        private static Dictionary<string, object> ParseObject(string s, ref int pos)
        {
            var dict = new Dictionary<string, object>();
            pos++; // {
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return dict; }

            while (true)
            {
                SkipWs(s, ref pos);
                string key = ParseString(s, ref pos);
                SkipWs(s, ref pos);
                if (s[pos] != ':') throw new FormatException($"expected ':' at {pos}");
                pos++;
                dict[key] = ParseValue(s, ref pos);
                SkipWs(s, ref pos);
                if (pos >= s.Length) throw new FormatException("unexpected end in object");
                if (s[pos] == ',') { pos++; continue; }

                if (s[pos] == '}') { pos++; return dict; }
                throw new FormatException($"expected ',' or '}}' at {pos}");
            }
        }

        private static List<object> ParseArray(string s, ref int pos)
        {
            var list = new List<object>();
            pos++; // [
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return list; }

            while (true)
            {
                list.Add(ParseValue(s, ref pos));
                SkipWs(s, ref pos);
                if (pos >= s.Length) throw new FormatException("unexpected end in array");
                if (s[pos] == ',') { pos++; continue; }

                if (s[pos] == ']') { pos++; return list; }
                throw new FormatException($"expected ',' or ']' at {pos}");
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            if (s[pos] != '"') throw new FormatException($"expected string at {pos}");
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    char e = s[pos++];
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
                            sb.Append((char)Convert.ToInt32(s.Substring(pos, 4), 16));
                            pos += 4;
                            break;
                        default: throw new FormatException($"bad escape \\{e}");
                    }
                }
                else sb.Append(c);
            }

            throw new FormatException("unterminated string");
        }

        private static object ParseNumber(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && ("+-0123456789.eE".IndexOf(s[pos]) >= 0)) pos++;
            string num = s.Substring(start, pos - start);
            if (num.Contains(".") || num.Contains("e") || num.Contains("E"))
                return double.Parse(num, CultureInfo.InvariantCulture);
            return long.Parse(num, CultureInfo.InvariantCulture);
        }
    }
}
