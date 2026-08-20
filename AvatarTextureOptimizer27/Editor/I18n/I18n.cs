using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Loads Localization/*.json. Auto follows NDMF culture, fallback en.
    /// 读取 json 本地化；Auto 跟随 NDMF，缺失回退英文。
    /// </summary>
    public static class I18n
    {
        static readonly Dictionary<string, Dictionary<string, string>> Tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        static bool _loaded;

        public static IEnumerable<string> Languages
        {
            get
            {
                Ensure();
                return Tables.Keys;
            }
        }

        public static string T(string lang, string key)
        {
            Ensure();
            lang = Resolve(lang);
            if (Tables.TryGetValue(lang, out var t) && t.TryGetValue(key, out var v)) return v;
            if (Tables.TryGetValue("en", out var en) && en.TryGetValue(key, out var ev)) return ev;
            return key;
        }

        public static string Resolve(string lang)
        {
            if (string.IsNullOrEmpty(lang) || lang == "Auto")
            {
#if NDMF || true
                try
                {
                    var culture = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                    if (Tables.ContainsKey(culture)) return culture;
                    if (culture == "zh") return Tables.ContainsKey("zh-Hans") ? "zh-Hans" : "en";
                }
                catch { /* ignore */ }
#endif
                return "en";
            }
            return lang;
        }

        static void Ensure()
        {
            if (_loaded) return;
            _loaded = true;
            var root = FindLocalizationFolder();
            if (root == null) return;
            foreach (var file in Directory.GetFiles(root, "*.json"))
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var json = File.ReadAllText(file);
                    Tables[name] = ParseFlatJson(json);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[ATO] i18n load failed " + file + " " + e.Message);
                }
            }
        }

        static string FindLocalizationFolder()
        {
            var guids = AssetDatabase.FindAssets("l:ATO_I18N_MARKER");
            foreach (var g in AssetDatabase.FindAssets("t:TextAsset"))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (p.Replace('\\', '/').Contains("/Localization/") && p.EndsWith(".json"))
                    return Path.GetDirectoryName(p);
            }
            // package relative
            var mono = MonoScript.FromScriptableObject(ScriptableObject.CreateInstance<Dummy>());
            return null;
        }

        class Dummy : ScriptableObject { }

        static Dictionary<string, string> ParseFlatJson(string json)
        {
            var dict = new Dictionary<string, string>();
            json = json.Trim();
            if (json.StartsWith("{")) json = json.Substring(1);
            if (json.EndsWith("}")) json = json.Substring(0, json.Length - 1);
            foreach (var part in SplitTop(json))
            {
                var kv = part.Split(new[] { ':' }, 2);
                if (kv.Length != 2) continue;
                dict[Unquote(kv[0])] = Unquote(kv[1]);
            }
            return dict;
        }

        static IEnumerable<string> SplitTop(string s)
        {
            var cur = new System.Text.StringBuilder();
            bool q = false;
            foreach (var ch in s)
            {
                if (ch == '"' && (cur.Length == 0 || cur[cur.Length - 1] != '\\')) q = !q;
                if (ch == ',' && !q)
                {
                    yield return cur.ToString();
                    cur.Length = 0;
                    continue;
                }
                cur.Append(ch);
            }
            if (cur.Length > 0) yield return cur.ToString();
        }

        static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"') s = s.Substring(1, s.Length - 2);
            return s.Replace("\\n", "\n").Replace("\\\"", "\"");
        }
    }
}
