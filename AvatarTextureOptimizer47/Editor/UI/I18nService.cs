using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.UI
{
    /// <summary>
    /// EN: Discovers JSON language packs anywhere in Assets or Packages.
    /// ZH: 在 Assets 或 Packages 任意位置发现 JSON 语言包。
    /// </summary>
    internal static class I18nService
    {
        [Serializable] private sealed class Entry { public string key; public string value; }
        [Serializable] private sealed class Pack { public string locale; public string displayName; public Entry[] entries; }

        internal sealed class Language
        {
            public string Locale;
            public string DisplayName;
            public Dictionary<string, string> Values;
        }

        private static List<Language> _languages;

        public static IReadOnlyList<Language> Languages
        {
            get
            {
                if (_languages == null) Reload();
                return _languages;
            }
        }

        public static void Reload()
        {
            var found = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
            foreach (var guid in AssetDatabase.FindAssets("ATO_i18n t:TextAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (asset == null) continue;
                try
                {
                    var pack = JsonUtility.FromJson<Pack>(asset.text);
                    if (pack == null || string.IsNullOrWhiteSpace(pack.locale)) continue;
                    found[pack.locale] = new Language
                    {
                        Locale = pack.locale,
                        DisplayName = string.IsNullOrWhiteSpace(pack.displayName) ? pack.locale : pack.displayName,
                        Values = (pack.entries ?? Array.Empty<Entry>())
                            .Where(x => x != null && !string.IsNullOrEmpty(x.key))
                            .GroupBy(x => x.key)
                            .ToDictionary(x => x.Key, x => x.Last().value),
                    };
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ATO] Invalid i18n file '{path}': {ex.Message}");
                }
            }

            _languages = found.Values.OrderBy(x => x.Locale, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string ResolveLocale(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) && !configured.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                return configured;

            try
            {
                // EN: NDMF 1.14.4 exposes its current language through LanguagePrefs.Language.
                // ZH: NDMF 1.14.4 通过 LanguagePrefs.Language 暴露当前语言。
                return nadena.dev.ndmf.localization.LanguagePrefs.Language;
            }
            catch
            {
                return "en-US";
            }
        }

        public static string Tr(string configuredLocale, string key)
        {
            var locale = ResolveLocale(configuredLocale);
            var exact = Languages.FirstOrDefault(x => x.Locale.Equals(locale, StringComparison.OrdinalIgnoreCase));
            var baseName = locale.Split('-')[0];
            var language = exact ?? Languages.FirstOrDefault(x => x.Locale.StartsWith(baseName + "-", StringComparison.OrdinalIgnoreCase))
                           ?? Languages.FirstOrDefault(x => x.Locale.Equals("en-US", StringComparison.OrdinalIgnoreCase));
            if (language != null && language.Values.TryGetValue(key, out var value)) return value;
            var english = Languages.FirstOrDefault(x => x.Locale.Equals("en-US", StringComparison.OrdinalIgnoreCase));
            return english != null && english.Values.TryGetValue(key, out value) ? value : key;
        }
    }
}
