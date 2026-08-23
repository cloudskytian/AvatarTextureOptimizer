using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Inspector
{
    /// <summary>JSON-backed localization with explicit English fallback. / JSON 本地化，缺失键回退英文。</summary>
    internal static class ATOI18n
    {
        private static readonly Dictionary<string, Dictionary<string, string>> Cache =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public static string ResolveLanguage(ATOLanguage selected)
        {
            if (selected == ATOLanguage.English) return "en";
            if (selected == ATOLanguage.SimplifiedChinese) return "zh-CN";
            return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
        }

        public static string Get(ATOLanguage selected, string key)
        {
            var language = ResolveLanguage(selected);
            var selectedTable = Load(language);
            if (selectedTable.TryGetValue(key, out var value)) return value;
            var english = Load("en"); return english.TryGetValue(key, out value) ? value : key;
        }

        private static Dictionary<string, string> Load(string language)
        {
            if (Cache.TryGetValue(language, out var table)) return table;
            table = new Dictionary<string, string>(StringComparer.Ordinal);
            var asset = Resources.Load<TextAsset>("I18n/" + language) ?? Resources.Load<TextAsset>("I18n/en");
            if (asset != null)
            foreach (Match match in Regex.Matches(asset.text,
                         "\\\"(?<key>(?:\\\\.|[^\\\"])*)\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\""))
                table[Unescape(match.Groups["key"].Value)] = Unescape(match.Groups["value"].Value);
            Cache[language] = table; return table;
        }

        private static string Unescape(string value) => Regex.Unescape(value.Replace("\\/", "/"));
    }
}
