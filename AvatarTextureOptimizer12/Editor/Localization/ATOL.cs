// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Localization front-end.
// AvatarTextureOptimizer (ATO) - 本地化前端。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Localization
{
    /// <summary>
    /// EN: User-extensible i18n. Any <c>TextAsset</c> in the project named <c>ato-lang.&lt;code&gt;.json</c>
    ///     (for example <c>ato-lang.fr.json</c>) is picked up automatically, so the language dropdown lists
    ///     exactly the languages present on disk. Missing keys fall back to English.
    /// ZH: 用户可扩展的 i18n。工程中任何命名为 <c>ato-lang.&lt;语言代码&gt;.json</c> 的 <c>TextAsset</c>
    ///     （例如 <c>ato-lang.fr.json</c>）都会被自动读取，因此语言下拉框中有几个配置文件就显示几个语言。
    ///     缺失的键回退到英文。
    /// </summary>
    public static class ATOL
    {
        public const string DefaultLanguage = "en";
        private const string FilePrefix = "ato-lang.";

        private static Localizer _localizer;
        private static Dictionary<string, Dictionary<string, string>> _tables;
        private static string[] _availableLanguages = { DefaultLanguage };

        /// <summary>EN: NDMF localizer used for error reports and UI. ZH: 用于错误报告与 UI 的 NDMF 本地化器。</summary>
        public static Localizer Localizer
        {
            get
            {
                if (_localizer == null) Reload();
                return _localizer;
            }
        }

        /// <summary>EN: Language codes discovered on disk. ZH: 磁盘上发现的语言代码。</summary>
        public static string[] AvailableLanguages
        {
            get
            {
                if (_tables == null) Reload();
                return _availableLanguages;
            }
        }

        /// <summary>
        /// EN: Language override chosen in the component inspector. Null / empty means "follow NDMF".
        /// ZH: 组件面板中选择的语言覆盖。为空表示跟随 NDMF。
        /// </summary>
        public static string ExplicitLanguage { get; set; }

        [InitializeOnLoadMethod]
        private static void Init() => Reload();

        /// <summary>EN: Rescan the project for translation files. ZH: 重新扫描工程内的翻译文件。</summary>
        public static void Reload()
        {
            _tables = LoadTables();
            _availableLanguages = _tables.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
            if (_availableLanguages.Length == 0) _availableLanguages = new[] { DefaultLanguage };

            _localizer = new Localizer(DefaultLanguage, () =>
            {
                var list = new List<(string, Func<string, string>)>();
                foreach (var kv in _tables)
                {
                    var table = kv.Value;
                    list.Add((kv.Key, key => table.TryGetValue(key, out var v) ? v : null));
                }
                if (list.Count == 0) list.Add((DefaultLanguage, _ => null));
                return list;
            });
        }

        private static Dictionary<string, Dictionary<string, string>> LoadTables()
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            string[] guids;
            try
            {
                guids = AssetDatabase.FindAssets("ato-lang t:TextAsset");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ATO] i18n scan failed: {e.Message}");
                return result;
            }

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var file = System.IO.Path.GetFileName(path);
                if (!file.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                var code = file.Substring(FilePrefix.Length, file.Length - FilePrefix.Length - ".json".Length);
                if (string.IsNullOrWhiteSpace(code)) continue;

                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (asset == null) continue;

                var table = ATOMiniJson.ParseFlat(asset.text, out var error);
                if (table == null)
                {
                    Debug.LogWarning($"[ATO] i18n file '{path}' is not valid JSON: {error}");
                    continue;
                }

                if (result.TryGetValue(code, out var existing))
                {
                    // EN: Later files merge over earlier ones, letting users patch shipped translations.
                    // ZH: 后加载的文件会覆盖先前的，允许用户对内置翻译打补丁。
                    foreach (var kv in table) existing[kv.Key] = kv.Value;
                }
                else
                {
                    result[code] = table;
                }
            }

            return result;
        }

        /// <summary>EN: Currently effective language code. ZH: 当前生效的语言代码。</summary>
        public static string CurrentLanguage
        {
            get
            {
                if (!string.IsNullOrEmpty(ExplicitLanguage)) return ExplicitLanguage;
                var ndmf = LanguagePrefs.Language;
                return string.IsNullOrEmpty(ndmf) ? DefaultLanguage : ndmf;
            }
        }

        /// <summary>
        /// EN: Look up a key, honouring the explicit language override, then the base language
        ///     (e.g. <c>zh</c> for <c>zh-CN</c>), then English, then the raw key.
        /// ZH: 查找一个键，依次尝试显式语言覆盖、基础语言（如 <c>zh-CN</c> 回退到 <c>zh</c>）、英文，最后返回键本身。
        /// </summary>
        public static string Tr(string key)
        {
            if (_tables == null) Reload();

            foreach (var candidate in CandidateLanguages())
            {
                if (_tables.TryGetValue(candidate, out var table) && table.TryGetValue(key, out var value))
                    return value;
            }
            return key;
        }

        /// <summary>EN: Look up and format. ZH: 查找并格式化。</summary>
        public static string Tr(string key, params object[] args)
        {
            var fmt = Tr(key);
            try { return string.Format(fmt, args); }
            catch (FormatException) { return fmt; }
        }

        /// <summary>EN: Convenience for inspector labels with tooltips. ZH: 便于生成带提示的面板标签。</summary>
        public static GUIContent G(string key)
        {
            return new GUIContent(Tr(key), Tr(key + ":tooltip") == key + ":tooltip" ? null : Tr(key + ":tooltip"));
        }

        private static IEnumerable<string> CandidateLanguages()
        {
            var lang = CurrentLanguage;
            yield return lang;

            var dash = lang.IndexOf('-');
            if (dash > 0) yield return lang.Substring(0, dash);

            // EN: Any regional variant of the same base language.
            // ZH: 同一基础语言的任意地区变体。
            var baseLang = dash > 0 ? lang.Substring(0, dash) : lang;
            foreach (var k in _availableLanguages)
            {
                if (k.StartsWith(baseLang + "-", StringComparison.OrdinalIgnoreCase)) yield return k;
            }

            yield return DefaultLanguage;
        }
    }
}
