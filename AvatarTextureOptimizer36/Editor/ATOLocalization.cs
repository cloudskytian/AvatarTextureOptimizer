using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using nadena.dev.ndmf.localization;
using Fosa.AvatarTextureOptimizer;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    [Serializable]
    internal sealed class ATOTranslationEntry
    {
        public string key;
        public string value;
    }

    [Serializable]
    internal sealed class ATOTranslationFile
    {
        public string language;
        public ATOTranslationEntry[] entries;
    }

    /// <summary>
    /// Loads user-extensible JSON localization files and falls back to English. / 读取用户可扩展 JSON 本地化文件并回退到英文。
    /// </summary>
    internal sealed class ATOLocalization
    {
        private readonly Dictionary<string, Dictionary<string, string>> _languages =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;

        public void Reload()
        {
            _loaded = true;
            _languages.Clear();
            string[] guids = AssetDatabase.FindAssets("t:TextAsset", new[] { "Packages/net.fosa.avatar-texture-optimizer/Editor/Resources/i18n" });
            for (int i = 0; i < guids.Length; i++) Load(AssetDatabase.GUIDToAssetPath(guids[i]));

            // Embedded-in-Assets copies are supported for local development and manual package sync. / 同时支持手动同步到 Assets 的本地开发副本。
            string[] fallbackGuids = AssetDatabase.FindAssets("t:TextAsset", new[] { "Assets" });
            for (int i = 0; i < fallbackGuids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(fallbackGuids[i]);
                if (path.EndsWith("/Editor/Resources/i18n/en.json", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("/Editor/Resources/i18n/zh-Hans.json", StringComparison.OrdinalIgnoreCase)) Load(path);
            }
        }

        public string Get(AvatarTextureOptimizer component, string key, string fallback)
        {
            if (!_loaded) Reload();
            string language = ResolveLanguage(component == null ? ATOLocalizationMode.Auto : component.localization);
            string value;
            Dictionary<string, string> table;
            if (_languages.TryGetValue(language, out table) && table.TryGetValue(key, out value)) return value;
            if (_languages.TryGetValue("en", out table) && table.TryGetValue(key, out value)) return value;
            return fallback;
        }

        private void Load(string path)
        {
            TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset == null) return;
            try
            {
                ATOTranslationFile file = JsonUtility.FromJson<ATOTranslationFile>(asset.text);
                if (file == null || string.IsNullOrEmpty(file.language)) return;
                Dictionary<string, string> table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (file.entries != null)
                    for (int i = 0; i < file.entries.Length; i++)
                        if (file.entries[i] != null && !string.IsNullOrEmpty(file.entries[i].key)) table[file.entries[i].key] = file.entries[i].value;
                _languages[file.language] = table;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[ATO] Could not load i18n file '" + path + "': " + exception.Message);
            }
        }

        private static string ResolveLanguage(ATOLocalizationMode mode)
        {
            if (mode == ATOLocalizationMode.English) return "en";
            if (mode == ATOLocalizationMode.SimplifiedChinese) return "zh-Hans";
            try
            {
                string language = LanguagePrefs.Language;
                if (!string.IsNullOrEmpty(language) && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
            }
            catch (Exception)
            {
                // NDMF language preferences are optional at edit time. / 编辑时 NDMF 语言偏好可能不可用。
            }
            return "en";
        }
    }
}
