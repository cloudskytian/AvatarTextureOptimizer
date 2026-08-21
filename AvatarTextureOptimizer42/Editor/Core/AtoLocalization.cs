using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Loads JSON localization files and keeps ATO aligned with NDMF language behavior.
    /// 读取 JSON 本地化文件，并让 ATO 的语言行为与 NDMF 保持一致。
    /// </summary>
    internal static class AtoLocalization
    {
        private const string AutoLanguageToken = "Auto";
        private static Localizer _localizer;

        public static Localizer Localizer => _localizer ??= new Localizer("en-US", Load);

        public static string AutoToken => AutoLanguageToken;

        public static string Translate(string key)
        {
            ApplyLanguageOverrideFromScene();
            return Localizer.GetLocalizedString(key);
        }

        public static string TranslateFormat(string key, params object[] args)
        {
            var raw = Translate(key);
            try
            {
                return string.Format(raw, args);
            }
            catch
            {
                return raw;
            }
        }

        public static IReadOnlyList<string> GetAvailableLanguages()
        {
            return Load()
                .Select(tuple => tuple.Item1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static string GetNativeLanguageName(string languageCode)
        {
            if (string.Equals(languageCode, AutoLanguageToken, StringComparison.OrdinalIgnoreCase))
            {
                return Translate("Inspector:AutoLanguage");
            }

            try
            {
                return CultureInfo.GetCultureInfo(languageCode).NativeName;
            }
            catch
            {
                return languageCode;
            }
        }

        public static void ApplyLanguageOverrideFromScene()
        {
            try
            {
                var active = Selection.activeGameObject;
                var component = active != null ? active.GetComponentInParent<AvatarTextureOptimizer>() : null;
                if (component == null)
                {
                    return;
                }

                if (string.Equals(component.Language, AutoLanguageToken, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var desired = component.Language.ToLowerInvariant();
                if (!string.Equals(LanguagePrefs.Language, desired, StringComparison.OrdinalIgnoreCase))
                {
                    LanguagePrefs.Language = desired;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ATO] Failed to apply language override. {ex.Message}");
            }
        }

        private static List<(string, Func<string, string>)> Load()
        {
            var packageRoot = AtoAssetLayout.FindPackageRoot();
            var localizationFolder = Path.Combine(packageRoot, "Editor", "Localization").Replace("\\", "/");
            var guids = AssetDatabase.FindAssets("t:TextAsset", new[] { localizationFolder });
            var files = new List<(string, Func<string, string>)>();

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (asset == null)
                {
                    continue;
                }

                var parsed = JsonUtility.FromJson<LocalizationFile>(asset.text);
                if (parsed == null || string.IsNullOrWhiteSpace(parsed.locale) || parsed.entries == null)
                {
                    continue;
                }

                var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in parsed.entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.key))
                    {
                        continue;
                    }

                    dictionary[entry.key] = entry.value ?? string.Empty;
                }

                files.Add((parsed.locale, key => dictionary.TryGetValue(key, out var value) ? value : null));
            }

            return files;
        }

        [Serializable]
        private sealed class LocalizationFile
        {
            public string locale;
            public LocalizationEntry[] entries;
        }

        [Serializable]
        private sealed class LocalizationEntry
        {
            public string key;
            public string value;
        }
    }
}
