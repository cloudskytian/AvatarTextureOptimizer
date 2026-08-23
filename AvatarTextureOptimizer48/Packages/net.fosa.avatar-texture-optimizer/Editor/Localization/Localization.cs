// ATO localization: reads json i18n files (one per language) and falls back to English.
// Auto mode reads NDMF's current language preference.
// / ATO 本地化：读取每个语言的 json i18n 文件，缺失时回退英文。Auto 模式读取 ndmf 当前语言。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using net.fosa.avatar_texture_optimizer.runtime;

namespace net.fosa.avatar_texture_optimizer.editor.localization
{
    /// <summary>
    /// Minimal localization service. / 简易本地化服务。
    /// </summary>
    public static class Localization
    {
        private static Dictionary<string, string> _en;
        private static Dictionary<string, string> _zh;
        private static bool _loaded;
        private static string _currentLang = "en-US";

        public static string CurrentLanguage => _currentLang;

        public static void Reload()
        {
            _loaded = false;
            _en = null;
            _zh = null;
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _en = LoadFile("en-US.json");
            _zh = LoadFile("zh-CN.json");
            if (_en == null) _en = new Dictionary<string, string>();
            if (_zh == null) _zh = new Dictionary<string, string>();
        }

        private static Dictionary<string, string> LoadFile(string file)
        {
            var path = "Packages/net.fosa.avatar-texture-optimizer/Editor/Localization/" + file;
            var ta = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (ta == null) return null;
            return ParseFlatJson(ta.text);
        }

        /// <summary>Parse a flat JSON object {"k":"v",...}. / 解析扁平 JSON 对象。</summary>
        internal static Dictionary<string, string> ParseFlatJson(string json)
        {
            var dict = new Dictionary<string, string>();
            int i = 0;
            while (i < json.Length)
            {
                // find '"key"'
                int ks = json.IndexOf('"', i);
                if (ks < 0) break;
                int ke = json.IndexOf('"', ks + 1);
                if (ke < 0) break;
                string key = json.Substring(ks + 1, ke - ks - 1);
                int colon = json.IndexOf(':', ke);
                if (colon < 0) break;
                int vs = json.IndexOf('"', colon);
                if (vs < 0) break;
                int ve = vs + 1;
                while (ve < json.Length)
                {
                    if (json[ve] == '\\') { ve += 2; continue; }
                    if (json[ve] == '"') break;
                    ve++;
                }
                string val = json.Substring(vs + 1, ve - vs - 1).Replace("\\n", "\n");
                dict[key] = val;
                i = ve + 1;
            }
            return dict;
        }

        /// <summary>Translate a key. / 翻译键。</summary>
        public static string T(string key)
        {
            EnsureLoaded();
            if (_currentLang.StartsWith("zh") && _zh != null && _zh.TryGetValue(key, out var zv)) return zv;
            if (_en != null && _en.TryGetValue(key, out var ev)) return ev;
            return key;
        }

        /// <summary>Set the language (auto resolves via NDMF LanguagePrefs). / 设置语言（Auto 通过 NDMF LanguagePrefs 解析）。</summary>
        public static void SetLanguage(AtoLanguage lang)
        {
            switch (lang)
            {
                case AtoLanguage.English:
                    _currentLang = "en-US";
                    break;
                case AtoLanguage.ChineseSimplified:
                    _currentLang = "zh-CN";
                    break;
                default:
                    _currentLang = ResolveNdmfLanguage();
                    break;
            }
        }

        private static string ResolveNdmfLanguage()
        {
            try
            {
                var t = System.Type.GetType("nadena.dev.ndmf.localization.LanguagePrefs, nadena.dev.ndmf");
                if (t != null)
                {
                    var prop = t.GetProperty("Language", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var val = prop != null ? prop.GetValue(null) as string : null;
                    if (!string.IsNullOrEmpty(val))
                    {
                        if (val.StartsWith("zh")) return "zh-CN";
                        if (val.StartsWith("ja")) return "en-US"; // no ja file yet -> English / 暂无日文 → 英文
                        return "en-US";
                    }
                }
            }
            catch (System.Exception)
            {
                // ignore / 忽略
            }
            switch (Application.systemLanguage)
            {
                case SystemLanguage.ChineseSimplified:
                case SystemLanguage.ChineseTraditional:
                case SystemLanguage.Chinese:
                    return "zh-CN";
                default:
                    return "en-US";
            }
        }
    }
}
