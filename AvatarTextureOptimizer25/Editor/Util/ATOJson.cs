// Avatar Texture Optimizer / 头像贴图优化器
// Minimal, dependency-free JSON parser/writer used for i18n files and the
// on-disk asset cache metadata. Handled subset: full JSON (objects, arrays,
// strings with \uXXXX escapes, numbers, booleans, null).
// 极简无依赖 JSON 解析/序列化器，用于 i18n 文件与磁盘缓存元数据。
// 支持完整 JSON 子集（对象、数组、含 \uXXXX 转义的字符串、数字、布尔、null）。
//
// Represented as: Dictionary<string,object> / List<object> / string / double / bool / null.
// 解析结果表示：Dictionary<string,object> / List<object> / string / double / bool / null。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Minimal JSON parser + writer. / 极简 JSON 解析与序列化。</summary>
    public static class ATOJson
    {
        // ---------------- Parser / 解析 ----------------

        /// <summary>Parses a JSON document. Throws FormatException on invalid input. / 解析 JSON 文本，非法输入抛 FormatException。</summary>
        public static object Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var p = new Parser(json);
            p.SkipWs();
            var v = p.ParseValue();
            p.SkipWs();
            if (!p.End) throw new FormatException("ATOJson: trailing content at " + p.Pos);
            return v;
        }

        /// <summary>Try-parse helper; returns defaultValue instead of throwing. / 容错解析，失败时返回 defaultValue。</summary>
        public static Dictionary<string, object> ParseObjectOrDefault(string json)
        {
            try
            {
                return Parse(json) as Dictionary<string, object>;
            }
            catch
            {
                return null;
            }
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            public int Pos => _i;
            public bool End => _i >= _s.Length;

            public Parser(string s) { _s = s; }

            public void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _i++;
                    else break;
                }
            }

            private char Peek()
            {
                if (End) throw new FormatException("ATOJson: unexpected end");
                return _s[_i];
            }

            private char Take() { char c = Peek(); _i++; return c; }

            private void Expect(char c)
            {
                if (Take() != c) throw new FormatException($"ATOJson: expected '{c}' at {_i}");
            }

            public object ParseValue()
            {
                SkipWs();
                char c = Peek();
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': ExpectWord("true"); return true;
                    case 'f': ExpectWord("false"); return false;
                    case 'n': ExpectWord("null"); return null;
                    default: return ParseNumber();
                }
            }

            private void ExpectWord(string w)
            {
                for (int k = 0; k < w.Length; k++) Expect(w[k]);
            }

            private Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                Expect('{');
                SkipWs();
                if (Peek() == '}') { Take(); return dict; }
                while (true)
                {
                    SkipWs();
                    string key = ParseString();
                    SkipWs();
                    Expect(':');
                    object val = ParseValue();
                    dict[key] = val;
                    SkipWs();
                    char c = Take();
                    if (c == ',') continue;
                    if (c == '}') break;
                    throw new FormatException("ATOJson: expected ',' or '}' at " + _i);
                }
                return dict;
            }

            private List<object> ParseArray()
            {
                var list = new List<object>();
                Expect('[');
                SkipWs();
                if (Peek() == ']') { Take(); return list; }
                while (true)
                {
                    list.Add(ParseValue());
                    SkipWs();
                    char c = Take();
                    if (c == ',') continue;
                    if (c == ']') break;
                    throw new FormatException("ATOJson: expected ',' or ']' at " + _i);
                }
                return list;
            }

            private string ParseString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (End) throw new FormatException("ATOJson: unterminated string");
                    char c = Take();
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    char e = Take();
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
                            if (_i + 4 > _s.Length) throw new FormatException("ATOJson: bad \\u escape");
                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: throw new FormatException("ATOJson: bad escape \\" + e);
                    }
                }
            }

            private double ParseNumber()
            {
                int start = _i;
                if (Peek() == '-') Take();
                while (!End && (char.IsDigit(_s[_i]) || _s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E' || _s[_i] == '+' || _s[_i] == '-'))
                {
                    // stop '+'/'-' unless following exponent / 指数符号之外停一下
                    if ((_s[_i] == '+' || _s[_i] == '-') && _i != start && _s[_i - 1] != 'e' && _s[_i - 1] != 'E') break;
                    _i++;
                }
                var text = _s.Substring(start, _i - start);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new FormatException("ATOJson: bad number '" + text + "'");
                return d;
            }
        }

        // ---------------- Writer / 序列化 ----------------

        /// <summary>Serializes a value graph (dict/list/string/number/bool/null). / 序列化值图。</summary>
        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        /// <summary>Escapes a single JSON string. / 转义单个 JSON 字符串。</summary>
        public static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            WriteString(sb, s);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: WriteString(sb, s); break;
                case IDictionary<string, object> d:
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in d)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        WriteValue(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                case System.Collections.IEnumerable list:
                    sb.Append('[');
                    bool f2 = true;
                    foreach (var item in list)
                    {
                        if (!f2) sb.Append(',');
                        f2 = false;
                        WriteValue(sb, item);
                    }
                    sb.Append(']');
                    break;
                case IFormattable fmt:
                    sb.Append(fmt.ToString(null, CultureInfo.InvariantCulture));
                    break;
                default:
                    WriteString(sb, v.ToString());
                    break;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------- Typed accessors / 类型化取值 ----------------

        public static string GetString(Dictionary<string, object> d, string key, string def = null)
        {
            if (d == null) return def;
            return d.TryGetValue(key, out var v) && v is string s ? s : def;
        }

        public static double GetNumber(Dictionary<string, object> d, string key, double def = 0)
        {
            if (d == null) return def;
            return d.TryGetValue(key, out var v) && v is double n ? n : def;
        }

        public static bool GetBool(Dictionary<string, object> d, string key, bool def = false)
        {
            if (d == null) return def;
            return d.TryGetValue(key, out var v) && v is bool b ? b : def;
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            return d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
        }
    }
}
