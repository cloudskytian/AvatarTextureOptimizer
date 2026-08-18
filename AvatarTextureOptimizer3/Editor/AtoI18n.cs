// English: Loads JSON i18n next to this package. Default Auto follows NDMF LanguagePrefs.
// 中文：读取包内 JSON 本地化。默认 Auto 跟随 NDMF LanguagePrefs。
using System;
using System.Collections.Generic;
using System.IO;
using nadena.dev.ndmf.localization;
using net.fosa.ato;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class AtoI18n
    {
        private static Dictionary<string, Dictionary<string, string>> _tables;
        private static AtoLanguageMode _mode = AtoLanguageMode.Auto;

        public static IEnumerable<string> AvailableLanguages
        {
            get
            {
                EnsureLoaded();
                return _tables.Keys;
            }
        }

        public static void SetMode(AtoLanguageMode mode) => _mode = mode;

        public static string T(string key)
        {
            EnsureLoaded();
            var lang = ResolveLang();
            if (_tables.TryGetValue(lang, out var t) && t.TryGetValue(key, out var s) && !string.IsNullOrEmpty(s))
                return s;
            if (_tables.TryGetValue("en-US", out var en) && en.TryGetValue(key, out var e) && !string.IsNullOrEmpty(e))
                return e;
            return key;
        }

        public static string T(string key, params object[] args)
        {
            try { return string.Format(T(key), args); }
            catch { return T(key); }
        }

        private static string ResolveLang()
        {
            if (_mode == AtoLanguageMode.English) return "en-US";
            if (_mode == AtoLanguageMode.SimplifiedChinese) return "zh-Hans";
            try
            {
                var cur = LanguagePrefs.Language;
                if (!string.IsNullOrEmpty(cur))
                {
                    if (cur.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
                    if (_tables != null && _tables.ContainsKey(cur)) return cur;
                    foreach (var k in _tables.Keys)
                        if (cur.StartsWith(k.Substring(0, Math.Min(2, k.Length)), StringComparison.OrdinalIgnoreCase))
                            return k;
                }
            }
            catch { /* NDMF language API not available */ }
            return "en-US";
        }

        private static void EnsureLoaded()
        {
            if (_tables != null) return;
            _tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var dir = Path.Combine(AtoPaths.PackageRoot, "Editor", "i18n");
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var map = MiniJson.ParseObject(json);
                    var locale = Path.GetFileNameWithoutExtension(file);
                    _tables[locale] = map;
                    try { LanguagePrefs.RegisterLanguage(locale); } catch { /* optional */ }
                }
                catch (Exception e)
                {
                    AtoLog.Warn("Failed to load i18n " + file + ": " + e.Message);
                }
            }
        }
    }

    internal static class AtoPaths
    {
        public static string PackageRoot
        {
            get
            {
                var script = UnityEditor.MonoScript.FromScriptableObject(ScriptableObject.CreateInstance<Marker>());
                var path = UnityEditor.AssetDatabase.GetAssetPath(script);
                UnityEngine.Object.DestroyImmediate(script);
                // Fallback: walk up from this file via AssetDatabase
                var guids = UnityEditor.AssetDatabase.FindAssets("t:asmdef net.fosa.avatar-texture-optimizer.editor");
                if (guids != null && guids.Length > 0)
                {
                    var p = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
                    return Path.GetDirectoryName(Path.GetDirectoryName(p));
                }
                return "Packages/net.fosa.avatar-texture-optimizer";
            }
        }

        private class Marker : ScriptableObject { }
    }

    /// <summary>Tiny JSON object parser for flat string dictionaries. / 扁平字符串字典的微型 JSON 解析器。</summary>
    internal static class MiniJson
    {
        public static Dictionary<string, string> ParseObject(string json)
        {
            var d = new Dictionary<string, string>();
            var obj = JsonUtility.FromJson<Wrap>("{\"raw\":0}");
            // Manual parse: "key": "value"
            int i = 0;
            while (i < json.Length)
            {
                int q1 = json.IndexOf('"', i);
                if (q1 < 0) break;
                int q2 = FindClose(json, q1 + 1);
                if (q2 < 0) break;
                var key = Unescape(json.Substring(q1 + 1, q2 - q1 - 1));
                int colon = json.IndexOf(':', q2 + 1);
                if (colon < 0) break;
                int q3 = json.IndexOf('"', colon + 1);
                if (q3 < 0) break;
                int q4 = FindClose(json, q3 + 1);
                if (q4 < 0) break;
                var val = Unescape(json.Substring(q3 + 1, q4 - q3 - 1));
                d[key] = val;
                i = q4 + 1;
            }
            return d;
        }

        private static int FindClose(string s, int start)
        {
            for (int i = start; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == '"') return i;
            }
            return -1;
        }

        private static string Unescape(string s) =>
            s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");

        [Serializable] private class Wrap { public int raw; }
    }
}
