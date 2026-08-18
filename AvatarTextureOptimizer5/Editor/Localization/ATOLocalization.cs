// Copyright (c) fosa. Licensed under the MIT License.
// Localization backed by JSON files under Editor/Resources/i18n. Adding a language is a matter
// of dropping in a new json file; no code change is required.
// 基于 Editor/Resources/i18n 下 JSON 文件的本地化。
// 新增语言只需放入一个新的 json 文件，无需修改代码。

using System;
using System.Collections.Generic;
using System.IO;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Central access point for all translated strings.
    /// 所有翻译字符串的统一访问入口。
    /// </summary>
    public static class ATOLocalization
    {
        /// <summary>Fallback language used when a key is missing. / 键缺失时使用的回退语言。</summary>
        public const string DefaultLanguage = "en";

        private const string ResourceFolder = "i18n";

        private static Localizer _localizer;

        /// <summary>
        /// The NDMF localizer. Created lazily so a missing resource folder cannot break domain
        /// reload.
        /// NDMF 本地化器。延迟创建，使资源文件夹缺失不会破坏域重载。
        /// </summary>
        public static Localizer Localizer
        {
            get
            {
                if (_localizer == null)
                {
                    _localizer = new Localizer(DefaultLanguage, LoadAllLanguages);
                }

                return _localizer;
            }
        }

        /// <summary>
        /// Translates a key, falling back to English and finally to the key itself so the UI is
        /// never blank.
        /// 翻译键，依次回退到英文、最后回退到键本身，使界面永不出现空白。
        /// </summary>
        public static string Tr(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;

            try
            {
                return Localizer.GetLocalizedString(key);
            }
            catch (Exception)
            {
                return key;
            }
        }

        /// <summary>
        /// Translates and formats a key.
        /// 翻译并格式化一个键。
        /// </summary>
        public static string Tr(string key, params object[] args)
        {
            var format = Tr(key);
            try
            {
                return string.Format(format, args);
            }
            catch (FormatException)
            {
                return format;
            }
        }

        /// <summary>
        /// Loads every language file found in the i18n resource folder.
        /// 加载 i18n 资源文件夹中找到的所有语言文件。
        /// </summary>
        private static List<(string, Func<string, string>)> LoadAllLanguages()
        {
            var result = new List<(string, Func<string, string>)>();
            var assets = Resources.LoadAll<TextAsset>(ResourceFolder);

            if (assets == null || assets.Length == 0)
            {
                // Never leave the localizer empty: an empty lookup would show raw keys.
                // 绝不让本地化器为空：空查找表会直接显示原始键。
                result.Add((DefaultLanguage, key => null));
                return result;
            }

            foreach (var asset in assets)
            {
                if (asset == null || string.IsNullOrEmpty(asset.text)) continue;

                var code = Path.GetFileNameWithoutExtension(asset.name);
                var table = ParseFlatJson(asset.text);
                if (table.Count == 0) continue;

                result.Add((code, key => table.TryGetValue(key, out var v) ? v : null));
            }

            if (result.Count == 0) result.Add((DefaultLanguage, key => null));
            return result;
        }

        /// <summary>
        /// Minimal flat JSON string-to-string parser. Unity's JsonUtility cannot deserialise a
        /// dictionary, and pulling in a JSON dependency for this would be disproportionate.
        /// 极简的扁平 JSON 字符串到字符串解析器。
        /// Unity 的 JsonUtility 无法反序列化字典，而为此引入 JSON 依赖并不划算。
        /// </summary>
        internal static Dictionary<string, string> ParseFlatJson(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(json)) return result;

            var i = 0;
            var n = json.Length;

            while (i < n)
            {
                // Find the next key string.
                // 查找下一个键字符串。
                while (i < n && json[i] != '"') i++;
                if (i >= n) break;

                var key = ReadString(json, ref i);
                if (key == null) break;

                // Skip to the colon.
                // 跳到冒号。
                while (i < n && json[i] != ':' && json[i] != '}') i++;
                if (i >= n || json[i] == '}') break;
                i++;

                // Skip whitespace before the value.
                // 跳过值之前的空白。
                while (i < n && char.IsWhiteSpace(json[i])) i++;
                if (i >= n) break;

                if (json[i] == '"')
                {
                    var value = ReadString(json, ref i);
                    if (value != null) result[key] = value;
                }
                else
                {
                    // Non-string values are not part of the schema; skip the token.
                    // 非字符串值不属于本 schema；跳过该 token。
                    while (i < n && json[i] != ',' && json[i] != '}') i++;
                }
            }

            return result;
        }

        /// <summary>
        /// Reads one JSON string literal starting at a quote, handling escapes.
        /// 从引号处读取一个 JSON 字符串字面量，并处理转义。
        /// </summary>
        private static string ReadString(string json, ref int i)
        {
            if (i >= json.Length || json[i] != '"') return null;
            i++;

            var sb = new System.Text.StringBuilder();

            while (i < json.Length)
            {
                var c = json[i];

                if (c == '\\')
                {
                    i++;
                    if (i >= json.Length) break;
                    var e = json[i];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 < json.Length &&
                                int.TryParse(
                                    json.Substring(i + 1, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    out var code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }

                            break;
                        default: sb.Append(e); break;
                    }

                    i++;
                    continue;
                }

                if (c == '"')
                {
                    i++;
                    return sb.ToString();
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }
    }
}
