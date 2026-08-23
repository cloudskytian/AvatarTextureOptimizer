// SPDX-License-Identifier: MIT
// EN: Tiny dependency free JSON reader for flat {"key":"value"} translation files.
// ZH: 用于扁平 {"key":"value"} 翻译文件的、无依赖的微型 JSON 读取器。

using System.Collections.Generic;
using System.Text;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Localization
{
    /// <summary>
    /// EN: Parses a flat JSON object of string to string. Nested objects are flattened with '.' so that
    ///     translators can group keys naturally. Anything that is not a string leaf is ignored.
    /// ZH: 解析字符串到字符串的扁平 JSON 对象。嵌套对象会以 '.' 连接展平，方便译者自然分组。
    ///     非字符串叶子节点会被忽略。
    /// </summary>
    public static class AtoMiniJson
    {
        /// <summary>
        /// EN: Parses the document. Returns <c>null</c> on malformed input rather than throwing, so a
        ///     single broken user translation cannot break the inspector.
        /// ZH: 解析文档。输入非法时返回 <c>null</c> 而不抛异常，
        ///     以免单个损坏的用户翻译导致检视面板不可用。
        /// </summary>
        public static Dictionary<string, string> ParseFlatStringMap(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var result = new Dictionary<string, string>();
            int i = 0;
            try
            {
                SkipWs(json, ref i);
                if (i >= json.Length || json[i] != '{') return null;
                ParseObject(json, ref i, "", result);
                return result;
            }
            catch
            {
                return null;
            }
        }

        private static void ParseObject(string s, ref int i, string prefix, Dictionary<string, string> outMap)
        {
            Expect(s, ref i, '{');
            SkipWs(s, ref i);
            if (s[i] == '}') { i++; return; }

            while (true)
            {
                SkipWs(s, ref i);
                var key = ParseString(s, ref i);
                SkipWs(s, ref i);
                Expect(s, ref i, ':');
                SkipWs(s, ref i);

                var full = prefix.Length == 0 ? key : prefix + "." + key;
                if (s[i] == '"') outMap[full] = ParseString(s, ref i);
                else if (s[i] == '{') ParseObject(s, ref i, full, outMap);
                else SkipValue(s, ref i);

                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                Expect(s, ref i, '}');
                return;
            }
        }

        private static void SkipValue(string s, ref int i)
        {
            if (s[i] == '[')
            {
                int depth = 0;
                do
                {
                    if (s[i] == '[') depth++;
                    else if (s[i] == ']') depth--;
                    else if (s[i] == '"') { ParseString(s, ref i); continue; }
                    i++;
                } while (depth > 0);
                return;
            }
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']') i++;
        }

        private static string ParseString(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (s[i] != '"')
            {
                if (s[i] == '\\')
                {
                    i++;
                    switch (s[i])
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            sb.Append((char)System.Convert.ToInt32(s.Substring(i + 1, 4), 16));
                            i += 4;
                            break;
                        default: sb.Append(s[i]); break;
                    }
                    i++;
                }
                else
                {
                    sb.Append(s[i++]);
                }
            }
            i++;
            return sb.ToString();
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        private static void Expect(string s, ref int i, char c)
        {
            SkipWs(s, ref i);
            if (s[i] != c) throw new System.FormatException($"expected '{c}' at {i}");
            i++;
        }
    }
}
