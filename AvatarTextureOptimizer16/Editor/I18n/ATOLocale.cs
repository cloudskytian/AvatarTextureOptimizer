using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// JSON-file based i18n. One language per JSON file; fallback to English when a key is missing.
    /// 基于 JSON 文件的 i18n。每种语言一个 JSON 文件；缺失键回退英文。
    /// </summary>
    public static class ATOLocale
    {
        private static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>();

        private static string _current = "Auto";
        private static bool _loaded = false;

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            var guids = AssetDatabase.FindAssets("t:TextAsset", new[] { "Assets", "Packages" });
            foreach (var g in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                if (!path.Contains("/AvatarTextureOptimizer/i18n/")) continue;
                var fileName = Path.GetFileNameWithoutExtension(path);
                try
                {
                    var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                    if (asset == null) continue;
                    var table = JsonUtility.FromJson<SerializableTable>(asset.text);
                    if (table != null && table.entries != null)
                    {
                        var dict = new Dictionary<string, string>();
                        foreach (var e in table.entries) dict[e.key] = e.value;
                        Tables[fileName] = dict;
                    }
                }
                catch { /* malformed json → ignore / 格式错误 → 忽略 */ }
            }
        }

        [System.Serializable]
        private sealed class SerializableTable { public List<Entry> entries; }

        [System.Serializable]
        private sealed class Entry { public string key; public string value; }

        public static void SetLanguage(string lang) => _current = lang;

        /// <summary>Translate a key; fallback to English. / 翻译键；回退英文。</summary>
        public static string T(string key)
        {
            EnsureLoaded();
            string lang = ResolveLanguage();
            if (Tables.TryGetValue(lang, out var t) && t.TryGetValue(key, out var v)) return v;
            if (Tables.TryGetValue("en", out var en) && en.TryGetValue(key, out var v2)) return v2;
            return key;
        }

        private static string ResolveLanguage()
        {
            if (_current != "Auto") return _current;
            // follow NDMF language when available / 可用时跟随 NDMF 语言
            try
            {
                var lang = nadena.dev.ndmf.localization.LanguagePrefs.Language;
                return lang switch
                {
                    "zh" or "zh-hans" => "zh-Hans",
                    _ => "en",           // ja/ko/zh-hant/en-us → fallback to English / 回退英文
                };
            }
            catch
            {
                return "en";
            }
        }
    }
}
