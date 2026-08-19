using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    // i18n 系统：读取 Localization/*.json（有几个配置文件就显示几个语言），支持用户扩展。
    // - 提供选项手动切换；"Auto" 读取 NDMF 当前语言配置（nadena.dev.ndmf.localization.LanguagePrefs.Language）；
    // - 缺失翻译回退英文；缺失 key 返回 key 本身并告警一次。
    // i18n system: loads Localization/*.json (as many languages as there are config files); user-extensible.
    // - Manual switching; "Auto" follows NDMF's language config; missing translations fall back to English.
    internal static class ATOLocalization
    {
        private static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private static string[] _available = Array.Empty<string>();
        private static bool _loaded;
        private static string _active = "en-us";
        private static readonly HashSet<string> WarnedKeys = new HashSet<string>();

        public static string[] AvailableLanguages
        {
            get { EnsureLoaded(); return _available; }
        }

        // 当前语言代码。Active language code.
        public static string ActiveLanguage
        {
            get { EnsureLoaded(); return _active; }
            set
            {
                EnsureLoaded();
                if (Tables.ContainsKey(value)) _active = value;
            }
        }

        // 应用组件上的语言设置（"Auto" → NDMF 语言）。Applies the component language setting ("Auto" → NDMF language).
        public static void ApplySetting(string setting)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(setting) || setting == "Auto") _active = ResolveAuto();
            else _active = Tables.ContainsKey(setting) ? setting : "en-us";
        }

        private static string ResolveAuto()
        {
            try
            {
                var ndmfLang = nadena.dev.ndmf.localization.LanguagePrefs.Language; // e.g. "en-us" / "zh-hans"
                if (Tables.ContainsKey(ndmfLang)) return ndmfLang;
                string head = ndmfLang.Split('-')[0];
                foreach (var lang in _available)
                {
                    if (lang.StartsWith(head, StringComparison.OrdinalIgnoreCase)) return lang;
                }
            }
            catch (Exception e)
            {
                ATOLog.Debug("NDMF 语言读取失败 / NDMF language read failed: " + e.Message);
            }
            return "en-us";
        }

        // 翻译（当前语言，缺失回退英文）。Translate with English fallback.
        public static string Tr(string key)
        {
            EnsureLoaded();
            Dictionary<string, string> t;
            string v;
            if (Tables.TryGetValue(_active, out t) && t.TryGetValue(key, out v)) return v;
            if (_active != "en-us" && Tables.TryGetValue("en-us", out t) && t.TryGetValue(key, out v)) return v;
            WarnMissing(key);
            return key;
        }

        // 带参数翻译。Translate with arguments.
        public static string Tr(string key, params object[] args)
        {
            var fmt = Tr(key);
            try
            {
                return string.Format(fmt, args);
            }
            catch (Exception)
            {
                return fmt;
            }
        }

        // 指定语言的原始翻译（供 NDMF Localizer 使用）。Raw translation for a specific language (for the NDMF Localizer).
        public static string Raw(string lang, string key)
        {
            EnsureLoaded();
            Dictionary<string, string> t;
            string v;
            if (Tables.TryGetValue(lang, out t) && t.TryGetValue(key, out v)) return v;
            if (lang != "en-us" && Tables.TryGetValue("en-us", out t) && t.TryGetValue(key, out v)) return v;
            return key;
        }

        private static void WarnMissing(string key)
        {
            if (WarnedKeys.Add(key)) ATOLog.Debug("i18n 缺失 key / missing key: " + key);
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            var dir = FindLocalizationDir();
            if (dir == null)
            {
                ATOLog.Warn("i18n 目录未找到 / localization dir not found");
                _active = "en-us";
                return;
            }
            try
            {
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    var lang = Path.GetFileNameWithoutExtension(file);
                    var text = File.ReadAllText(file, System.Text.Encoding.UTF8);
                    Tables[lang] = ATOSimpleJson.ParseObject(text);
                }
            }
            catch (Exception e)
            {
                ATOLog.Warn("i18n 加载失败 / failed: " + e.Message);
            }

            var langs = new List<string>(Tables.Keys);
            langs.Sort(StringComparer.OrdinalIgnoreCase);
            if (!Tables.ContainsKey("en-us"))
            {
                if (langs.Count > 0) Tables["en-us"] = Tables[langs[0]];
                else langs.Add("en-us");
            }
            _available = langs.ToArray();
            _active = ResolveAuto();
        }

        // 通过自身脚本位置定位包内 Localization 目录（安装位置无关）。
        // Locates the package's Localization dir via this script's own location (install-location independent).
        private static string FindLocalizationDir()
        {
            var guids = AssetDatabase.FindAssets("ATOLocalization t:MonoScript");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.EndsWith("ATOLocalization.cs")) continue;
                var dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), "..", "..", "Localization"));
                if (Directory.Exists(dir)) return dir;
            }
            return null;
        }
    }
}
