// User-extensible i18n: loads every Localization/*.json next to the package.
// 可由用户扩展的 i18n：读取包内 Localization/ 下全部 json 配置文件。
// Language = component override > NDMF current language > English fallback.
// 语言 = 组件覆写 > NDMF 当前语言 > 英文回退。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class ATOL10n
    {
        /// <summary>language code -> (key -> text). / 语言码 -> 键值表。</summary>
        private static Dictionary<string, Dictionary<string, string>> _tables;

        /// <summary>All discovered language codes (from json files). / 发现的全部语言码。</summary>
        internal static IReadOnlyList<string> Languages
        {
            get
            {
                EnsureLoaded();
                return _tables.Keys.OrderBy(k => k).ToArray();
            }
        }

        internal static void Reload()
        {
            _tables = null;
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_tables != null) return;
            _tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in FindLocalizationFiles())
            {
                try
                {
                    string lang = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    string text = File.ReadAllText(file);
                    if (!(MiniJson.Parse(text) is Dictionary<string, object> root)) continue;
                    var table = new Dictionary<string, string>();
                    foreach (var kv in root)
                        if (kv.Value is string s)
                            table[kv.Key] = s;
                    _tables[lang] = table;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ATO] failed to parse i18n file {file}: {e.Message}");
                }
            }

            if (_tables.Count == 0)
                Debug.LogWarning("[ATO] no Localization/*.json found; English hardcoded fallback in use");
        }

        private static IEnumerable<string> FindLocalizationFiles()
        {
            // 1) VPM package path / 包形式
            const string pkgDir = "Packages/net.fosa.avatar-texture-optimizer/Localization";
            if (Directory.Exists(pkgDir))
                return Directory.GetFiles(pkgDir, "*.json");

            // 2) dropped into Assets (folder name search) / 同步进 Assets 的场合
            var dir = Directory.GetFiles("Assets", "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith("Localization/en.json", StringComparison.OrdinalIgnoreCase)
                            || p.EndsWith("Localization/zh-hans.json", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetDirectoryName)
                .FirstOrDefault();
            return dir == null ? Array.Empty<string>() : Directory.GetFiles(dir, "*.json");
        }

        /// <summary>Resolve current language code. / 解析当前语言码。</summary>
        internal static string ResolveLanguage(string overrideCode)
        {
            EnsureLoaded();
            if (!string.IsNullOrEmpty(overrideCode) && _tables.ContainsKey(overrideCode))
                return overrideCode;

            string ndmfLang = "";
            try
            {
                ndmfLang = (LanguagePrefs.Language ?? "en-us").ToLowerInvariant();
            }
            catch
            {
                ndmfLang = "en";
            }

            // normalize: en-us -> en, zh-cn/zh-hans -> zh-hans / 归一化
            string norm = ndmfLang.StartsWith("zh") ? "zh-hans" : ndmfLang.Split('-')[0];
            if (_tables.ContainsKey(norm)) return norm;
            if (_tables.Count > 0 && !_tables.ContainsKey("en")) return _tables.Keys.First();
            return "en";
        }

        /// <summary>Translate a key in the given language with English fallback.
        /// 按语言翻译键，缺失回退英文。</summary>
        internal static string Get(string key, string lang = null)
        {
            EnsureLoaded();
            lang = lang ?? ResolveLanguage(null);
            if (_tables.TryGetValue(lang, out var t) && t.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                return v;
            if (_tables.TryGetValue("en", out var en) && en.TryGetValue(key, out var v2))
                return v2;
            return key;
        }

        // ------------------------------------------------------------------
        // NDMF Localizer bridge (for ErrorReport keys). / NDMF Localizer 桥接。
        // ------------------------------------------------------------------
        private static Localizer _ndmfLocalizer;

        internal static Localizer NdmfLocalizer
        {
            get
            {
                if (_ndmfLocalizer != null) return _ndmfLocalizer;
                _ndmfLocalizer = new Localizer("en", () =>
                {
                    EnsureLoaded();
                    return _tables.Select(kv =>
                            (kv.Key, (Func<string, string>)(k =>
                                kv.Value.TryGetValue(k, out var v) ? v : null)))
                        .ToList();
                });
                return _ndmfLocalizer;
            }
        }
    }
}
