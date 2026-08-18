using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// JSON i18n. Auto follows NDMF language. / JSON 本地化，Auto 跟随 NDMF。
    /// </summary>
    public static class AtoI18n
    {
        static readonly Dictionary<string, Dictionary<string, string>> Tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        static bool _loaded;
        static AtoLanguageMode _override = AtoLanguageMode.Auto;

        public static void SetMode(AtoLanguageMode mode)
        {
            _override = mode;
        }

        public static IEnumerable<string> AvailableLanguages
        {
            get
            {
                EnsureLoaded();
                return Tables.Keys;
            }
        }

        public static string CurrentLang
        {
            get
            {
                EnsureLoaded();
                if (_override == AtoLanguageMode.English) return "en";
                if (_override == AtoLanguageMode.ChineseSimplified) return "zh-Hans";
                var ndmf = TryNdmfLanguage();
                if (!string.IsNullOrEmpty(ndmf) && Tables.ContainsKey(ndmf)) return ndmf;
                if (!string.IsNullOrEmpty(ndmf) && ndmf.StartsWith("zh") && Tables.ContainsKey("zh-Hans"))
                    return "zh-Hans";
                return "en";
            }
        }

        public static string T(string key)
        {
            EnsureLoaded();
            var lang = CurrentLang;
            if (Tables.TryGetValue(lang, out var t) && t.TryGetValue(key, out var s) && !string.IsNullOrEmpty(s))
                return s;
            if (Tables.TryGetValue("en", out var en) && en.TryGetValue(key, out var e) && !string.IsNullOrEmpty(e))
                return e;
            return key;
        }

        static string TryNdmfLanguage()
        {
            try
            {
                var type = Type.GetType("nadena.dev.ndmf.localization.LanguagePrefs, nadena.dev.ndmf");
                if (type == null) return null;
                var prop = type.GetProperty("Language", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return prop?.GetValue(null) as string;
            }
            catch
            {
                return null;
            }
        }

        static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            var root = FindPackageRoot();
            if (root == null) return;
            var dir = Path.Combine(root, "Runtime", "Localization");
            if (!Directory.Exists(dir)) dir = Path.Combine(root, "Editor", "Localization");
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var json = File.ReadAllText(file);
                    var dict = ParseFlatJson(json);
                    Tables[name] = dict;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ATO] failed to load i18n {file}: {ex.Message}");
                }
            }
        }

        internal static void Reload()
        {
            _loaded = false;
            Tables.Clear();
            EnsureLoaded();
        }

        static string FindPackageRoot()
        {
            var guids = AssetDatabase.FindAssets("t:asmdef net.fosa.avatar-texture-optimizer.editor");
            foreach (var g in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                var dir = Path.GetDirectoryName(path);
                if (dir != null && dir.EndsWith("Editor"))
                    return Path.GetDirectoryName(dir);
            }
            return null;
        }

        /// <summary>
        /// Minimal flat JSON object parser (string:string). / 扁平 JSON 解析。
        /// </summary>
        static Dictionary<string, string> ParseFlatJson(string json)
        {
            var d = new Dictionary<string, string>();
            int i = 0;
            void SkipWs()
            {
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            }
            string ReadString()
            {
                if (json[i] != '"') return null;
                i++;
                var sb = new System.Text.StringBuilder();
                while (i < json.Length)
                {
                    var c = json[i++];
                    if (c == '\\' && i < json.Length)
                    {
                        var n = json[i++];
                        sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n == '"' ? '"' : n == '\\' ? '\\' : n);
                    }
                    else if (c == '"') break;
                    else sb.Append(c);
                }
                return sb.ToString();
            }
            SkipWs();
            if (i >= json.Length || json[i] != '{') return d;
            i++;
            while (i < json.Length)
            {
                SkipWs();
                if (i < json.Length && json[i] == '}') break;
                var k = ReadString();
                SkipWs();
                if (i < json.Length && json[i] == ':') i++;
                SkipWs();
                var v = ReadString();
                if (k != null && v != null) d[k] = v;
                SkipWs();
                if (i < json.Length && json[i] == ',') i++;
            }
            return d;
        }
    }
}
