// ATO — Avatar Texture Optimizer
// A tiny, dependency-free JSON parser used to read the i18n configuration files.
// 一个零依赖的迷你 JSON 解析器，用于读取 i18n 配置文件。
//
// Why not Newtonsoft / System.Text.Json? Unity's .NET Standard 2.1 profile does not
// include System.Text.Json by default, and Newtonsoft is an extra package we do not
// want to force on users. This parser covers the JSON subset needed for flat
// string→string localization files (objects, arrays, strings, numbers, booleans, null).
// 为什么不用 Newtonsoft / System.Text.Json？Unity 的 .NET Standard 2.1 默认不含 System.Text.Json，
// 而 Newtonsoft 是需要额外安装的包，我们不想强加给用户。此解析器覆盖本地化扁平 string→string
// 文件所需的 JSON 子集（对象、数组、字符串、数字、布尔、null）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Minimal JSON value model. 迷你 JSON 值模型。
    /// </summary>
    public static class ATOMiniJson
    {
        /// <summary>
        /// Parse a JSON text into object / List&lt;object&gt; / string / double / bool / null.
        /// 将 JSON 文本解析为 object / List&lt;object&gt; / string / double / bool / null。
        /// </summary>
        public static object Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var p = new Parser(json);
            object result = p.ParseValue();
            p.SkipWhitespace();
            if (!p.AtEnd) throw new FormatException($"Unexpected trailing content at index {p.Index}.");
            return result;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) { _s = s; }
            public int Index => _i;
            public bool AtEnd => _i >= _s.Length;

            public void SkipWhitespace()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\ufeff') _i++;
                    else break;
                }
            }

            private char Peek()
            {
                SkipWhitespace();
                if (AtEnd) throw new FormatException("Unexpected end of JSON.");
                return _s[_i];
            }

            public object ParseValue()
            {
                char c = Peek();
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ParseNumber();
                }
            }

            private Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                Expect('{');
                SkipWhitespace();
                if (!AtEnd && _s[_i] == '}') { _i++; return dict; }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd) throw new FormatException("Unterminated JSON object.");
                    if (_s[_i] != '"') throw new FormatException($"Expected string key at index {_i}.");
                    string key = ParseString();
                    SkipWhitespace();
                    if (AtEnd || _s[_i] != ':') throw new FormatException($"Expected ':' at index {_i}.");
                    _i++;
                    dict[key] = ParseValue();
                    SkipWhitespace();
                    if (AtEnd) throw new FormatException("Unterminated JSON object.");
                    char c = _s[_i];
                    if (c == ',') { _i++; continue; }
                    if (c == '}') { _i++; return dict; }
                    throw new FormatException($"Expected ',' or '}}' at index {_i}.");
                }
            }

            private List<object> ParseArray()
            {
                var list = new List<object>();
                Expect('[');
                SkipWhitespace();
                if (!AtEnd && _s[_i] == ']') { _i++; return list; }
                while (true)
                {
                    list.Add(ParseValue());
                    SkipWhitespace();
                    if (AtEnd) throw new FormatException("Unterminated JSON array.");
                    char c = _s[_i];
                    if (c == ',') { _i++; continue; }
                    if (c == ']') { _i++; return list; }
                    throw new FormatException($"Expected ',' or ']' at index {_i}.");
                }
            }

            private string ParseString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw new FormatException("Unterminated JSON string.");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (AtEnd) throw new FormatException("Unterminated escape.");
                        char e = _s[_i++];
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
                                if (_i + 4 > _s.Length) throw new FormatException("Invalid \\u escape.");
                                sb.Append((char)ushort.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                _i += 4;
                                break;
                            default: throw new FormatException($"Invalid escape '\\{e}'.");
                        }
                    }
                    else sb.Append(c);
                }
            }

            private double ParseNumber()
            {
                int start = _i;
                if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E' || _s[_i] == '-' || _s[_i] == '+')) _i++;
                string token = _s.Substring(start, _i - start);
                if (token.Length == 0) throw new FormatException($"Invalid number at index {start}.");
                return double.Parse(token, CultureInfo.InvariantCulture);
            }

            private void Expect(char c)
            {
                SkipWhitespace();
                if (AtEnd || _s[_i] != c) throw new FormatException($"Expected '{c}' at index {_i}.");
                _i++;
            }

            private void Expect(string word)
            {
                SkipWhitespace();
                if (_i + word.Length > _s.Length || string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
                    throw new FormatException($"Expected '{word}' at index {_i}.");
                _i += word.Length;
            }
        }
    }
}
