using System.Collections.Generic;
using System.Text;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    // 极简 JSON 对象解析器：仅支持单层 string→string 映射（专用于 i18n 表，避免依赖第三方 JSON 库）。
    // Minimal JSON object parser: flat string→string maps only (for i18n tables; avoids third-party JSON dependencies).
    internal static class ATOSimpleJson
    {
        public static Dictionary<string, string> ParseObject(string json)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(json)) return result;
            int i = 0;
            SkipWs(json, ref i);
            if (i >= json.Length || json[i] != '{') return result;
            i++;
            while (true)
            {
                SkipWs(json, ref i);
                if (i >= json.Length) return result;
                if (json[i] == '}') return result;
                if (json[i] == ',') { i++; continue; }
                string key = ParseString(json, ref i);
                SkipWs(json, ref i);
                if (i >= json.Length || json[i] != ':') return result;
                i++;
                SkipWs(json, ref i);
                string value = ParseString(json, ref i);
                result[key] = value;
            }
        }

        private static string ParseString(string json, ref int i)
        {
            SkipWs(json, ref i);
            if (i >= json.Length || json[i] != '"') return "";
            i++;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '"') { i++; return sb.ToString(); }
                if (c == '\\' && i + 1 < json.Length)
                {
                    char e = json[++i];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 < json.Length)
                            {
                                sb.Append((char)System.Convert.ToInt32(json.Substring(i + 1, 4), 16));
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
                i++;
            }
            return sb.ToString();
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }
    }
}
