// SPDX-License-Identifier: MIT
// EN: Tiny dependency free JSON reader for flat {"key":"value"} localisation files.
// ZH: 用于扁平 {"key":"value"} 本地化文件的极简 JSON 读取器（无外部依赖）。

using System.Collections.Generic;
using System.Text;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: Parses a flat JSON object of string values. Nested objects are flattened with ':' separators so a
    ///     translator may group keys. Anything else is ignored gracefully.
    /// ZH: 解析扁平的字符串 JSON 对象。嵌套对象会用 ':' 连接展平，方便译者分组。其他内容会被安全忽略。
    /// </summary>
    public static class ATOJson
    {
        public static Dictionary<string, string> ParseFlatStringMap(string text)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(text)) return result;

            var i = 0;
            SkipWs(text, ref i);
            if (i >= text.Length || text[i] != '{') return result;
            i++;
            ParseObject(text, ref i, "", result);
            return result;
        }

        private static void ParseObject(string s, ref int i, string prefix, Dictionary<string, string> outMap)
        {
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) return;
                if (s[i] == '}')
                {
                    i++;
                    return;
                }

                if (s[i] == ',')
                {
                    i++;
                    continue;
                }

                if (s[i] != '"') return; // malformed, bail out safely
                var key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') return;
                i++;
                SkipWs(s, ref i);
                if (i >= s.Length) return;

                var fullKey = prefix.Length == 0 ? key : prefix + ":" + key;
                if (s[i] == '"')
                {
                    outMap[fullKey] = ParseString(s, ref i);
                }
                else if (s[i] == '{')
                {
                    i++;
                    ParseObject(s, ref i, fullKey, outMap);
                }
                else
                {
                    SkipValue(s, ref i);
                }
            }
        }

        private static void SkipValue(string s, ref int i)
        {
            var depth = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '[' || c == '{') depth++;
                else if (c == ']' || c == '}')
                {
                    if (depth == 0) return;
                    depth--;
                }
                else if (c == ',' && depth == 0) return;
                else if (c == '"')
                {
                    ParseString(s, ref i);
                    continue;
                }

                i++;
            }
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
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
                                int.TryParse(s.Substring(i, 4), System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out var code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }

                            break;
                        default: sb.Append(e); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }
    }
}
